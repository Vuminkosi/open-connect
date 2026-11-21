"""Check an explicitly provisioned NLLB model without fetching or modifying it."""

from openconnect_nllb.config import Settings
from openconnect_nllb.decoder_bundle import resolve_decoder_bundle


def main() -> int:
    settings = Settings.from_environment()
    if settings.fake_engine:
        print("NLLB fake engine selected for non-production use; no model is provisioned.")
        return 0
    if not settings.model_id:
        print("NLLB model is not selected; set OC_NLLB_MODEL_ID.")
        return 1
    bundle, reason = resolve_decoder_bundle(
        settings.model_dir, settings.model_id, license_accepted=settings.license_accepted,
        commercial_use=settings.commercial_use,
    )
    if bundle is None:
        print(f"NLLB model is not ready: {reason or 'model_unavailable'}.")
        return 1
    print("NLLB active model assets and license are verified; decoder readiness is checked by the worker.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
