from pydantic import BaseModel, ConfigDict, Field


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Capability(StrictModel):
    target: str = Field(min_length=1, max_length=256)
    action: str = Field(min_length=1, max_length=32)
    label: str | None = Field(default=None, min_length=1, max_length=256)


class Context(StrictModel):
    route: str = Field(min_length=1, max_length=256)
    availableActions: list[Capability] = Field(max_length=256)


class Generation(StrictModel):
    maxActions: int = Field(default=8, ge=1, le=32)
    maxOutputTokens: int = Field(default=256, ge=16, le=512)


class PlanRequest(StrictModel):
    requestId: str = Field(min_length=1, max_length=128)
    schemaVersion: str = Field(min_length=1, max_length=32)
    intent: str = Field(min_length=1, max_length=8000)
    context: Context
    generation: Generation = Generation()


class PlannedAction(StrictModel):
    type: str = Field(min_length=1, max_length=32)
    target: str = Field(min_length=1, max_length=256)
    value: str | None = Field(default=None, max_length=256)


class ActionPlan(StrictModel):
    schemaVersion: str
    actions: list[PlannedAction]


class PlanResponse(StrictModel):
    requestId: str
    schemaVersion: str
    provider: str
    status: str
    plan: ActionPlan
