import asyncio
import logging
import secrets
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager, suppress

from fastapi import FastAPI, Header, HTTPException, Request
from pydantic import ValidationError

from .inference import ensure_model, load_model
from .schemas import PlanRequest, PlanResponse
from .settings import Settings


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = Settings.from_environment()
    settings.validate()
    app.state.settings = settings
    app.state.engine = None
    app.state.ready = False
    app.state.startup_task = None
    app.state.executor = ThreadPoolExecutor(max_workers=settings.max_concurrent, thread_name_prefix="liveui-inference")
    app.state.slots = asyncio.Semaphore(settings.max_concurrent + settings.queue_capacity)
    if settings.enabled:
        app.state.startup_task = asyncio.create_task(initialize_model(app), name="liveui-model-initialization")
    try:
        yield
    finally:
        app.state.ready = False
        if app.state.startup_task is not None and not app.state.startup_task.done():
            app.state.startup_task.cancel()
            with suppress(asyncio.CancelledError):
                await app.state.startup_task
        await asyncio.to_thread(lambda: app.state.executor.shutdown(wait=True, cancel_futures=True))


async def initialize_model(app: FastAPI) -> None:
    try:
        settings = app.state.settings
        model_path = await ensure_model(settings)
        app.state.engine = await asyncio.get_running_loop().run_in_executor(
            app.state.executor, load_model, settings, model_path
        )
        app.state.ready = True
    except asyncio.CancelledError:
        raise
    except Exception:
        app.state.ready = False


app = FastAPI(title="OpenConnect LiveUI Inference Worker", version="1.0.0", lifespan=lifespan)
logger = logging.getLogger(__name__)


@app.get("/health/live")
async def liveness():
    return {"status": "live"}


@app.get("/health/ready")
async def readiness(request: Request):
    if not request.app.state.ready:
        raise HTTPException(status_code=503, detail="model_not_ready")
    return {"status": "ready"}


@app.post("/v1/liveui/inference/plan", response_model=PlanResponse)
async def generate_plan(payload: PlanRequest, request: Request, authorization: str | None = Header(default=None)):
    settings = request.app.state.settings
    if (
        not settings.bearer_token
        or authorization is None
        or not authorization.startswith("Bearer ")
        or not secrets.compare_digest(authorization[7:], settings.bearer_token)
    ):
        raise HTTPException(status_code=401, detail="unauthorized")
    if not request.app.state.ready or request.app.state.engine is None:
        raise HTTPException(status_code=503, detail="model_unavailable")
    acquired = False
    future = None
    try:
        await asyncio.wait_for(request.app.state.slots.acquire(), timeout=0.1)
        acquired = True
    except TimeoutError as error:
        raise HTTPException(status_code=429, detail="inference_queue_full") from error
    try:
        loop = asyncio.get_running_loop()
        future = loop.run_in_executor(request.app.state.executor, request.app.state.engine.generate, payload)
        plan, provider = await asyncio.wait_for(
            asyncio.shield(future),
            timeout=request.app.state.settings.request_timeout_seconds,
        )
        return PlanResponse(
            requestId=payload.requestId,
            schemaVersion=payload.schemaVersion,
            provider=provider,
            status="completed",
            plan=plan,
        )
    except TimeoutError as error:
        if future is not None and not future.done():
            acquired = False
            future.add_done_callback(lambda _: request.app.state.slots.release())
        raise HTTPException(status_code=504, detail="inference_timeout") from error
    except asyncio.CancelledError:
        if future is not None and not future.done():
            acquired = False
            future.add_done_callback(lambda _: request.app.state.slots.release())
        raise
    except (ValidationError, ValueError) as error:
        raise HTTPException(status_code=422, detail="invalid_generated_plan") from error
    except Exception as error:
        logger.error("LiveUI inference failed (%s)", type(error).__name__)
        raise HTTPException(status_code=503, detail="inference_failed") from error
    finally:
        if acquired:
            request.app.state.slots.release()
