"""Check that local Compose never downloads models without explicit provisioning."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which("docker"), "Docker Compose is required")
class ModelProvisioningDefaultsTests(unittest.TestCase):
    def configuration(self, override: bool) -> dict:
        env = os.environ.copy()
        env.pop("OC_MODEL_PROVISIONING", None)
        env.pop("OC_MODEL_OFFLINE", None)
        env["OPENCONNECT_WORKER_TOKEN"] = "test-only-placeholder"
        if override:
            env.update(OC_MODEL_PROVISIONING="first-start-if-missing", OC_MODEL_OFFLINE="false")
        with tempfile.TemporaryDirectory(prefix="openconnect-compose-test-") as directory:
            env_file = Path(directory) / "empty.env"
            env_file.write_text("", encoding="utf-8")
            result = subprocess.run(
                ["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                 "--profile", "*", "config", "--format", "json"],
                cwd=ROOT, env=env, capture_output=True, text=True, check=True,
            )
        return json.loads(result.stdout)["services"]

    def test_models_default_to_offline_existing_assets(self) -> None:
        count = 0
        for service in self.configuration(False).values():
            values = service.get("environment") or {}
            if "OC_MODEL_PROVISIONING" in values:
                self.assertEqual("verify-existing-only", values["OC_MODEL_PROVISIONING"])
                count += 1
            if "OC_MODEL_OFFLINE" in values:
                self.assertEqual("true", str(values["OC_MODEL_OFFLINE"]).lower())
        self.assertGreater(count, 0)

    def test_explicit_provisioning_override_is_forwarded(self) -> None:
        values = [service.get("environment") or {} for service in self.configuration(True).values()]
        modes = [item["OC_MODEL_PROVISIONING"] for item in values if "OC_MODEL_PROVISIONING" in item]
        self.assertTrue(modes)
        self.assertTrue(all(mode == "first-start-if-missing" for mode in modes))


if __name__ == "__main__":
    unittest.main()
