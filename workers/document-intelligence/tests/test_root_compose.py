import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
@pytest.mark.parametrize("profile", ["documents", "vision", "vision-embed", "storage", "knowledge",
                                     "knowledge-index", "knowledge-image", "knowledge-rerank", "knowledge-migrate",
                                     "storage-init", "analytics", "analytics-cloud", "knowledge-cloud", "extraction"])
def test_expansion_profile_resolves_independently(profile, tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env["OPENCONNECT_WORKER_TOKEN"] = "compose-validation-token"
    subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                    "--profile", profile, "config", "--quiet"], cwd=root, env=env, capture_output=True, check=True, timeout=30)


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_analytics_cloud_proxy_is_private_and_requires_claim_endpoint(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env["OC_ANALYTICS_CLOUD_CONSUMPTION_ENABLED"] = "false"
    env["OPENCONNECT_WORKER_TOKEN"] = "compose-validation-token"
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "--profile", "analytics-cloud", "config", "--format", "json"],
                            cwd=root, env=env, capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    assert "ports" not in services["worker-analytics"]
    assert "ports" not in services["analytics-tls"]
    assert services["worker-analytics"]["environment"]["OPENCONNECT_CLOUD_CONSUMPTION_ENABLED"] == "false"
    config = (root / "cloud/analytics-tls/nginx.conf").read_text(encoding="utf-8")
    assert "ssl_verify_client on;" in config
    assert "location = /internal/cloud/v1/analytics/execute" in config
    assert "location = /internal/v1/execute" not in config


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_knowledge_cloud_proxy_is_private_and_requires_client_certificate(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env.update({"OPENCONNECT_WORKER_TOKEN": "compose-validation-token",
                "OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED": "false",
                "CLOUD_KNOWLEDGE_EMBEDDING_EXECUTION_ENABLED": "false",
                "CLOUD_KNOWLEDGE_EMBEDDING_ORIGIN": "https://knowledge-embedding-tls:8443",
                "OC_KNOWLEDGE_CLOUD_SECRETS_DIR": str(tmp_path),
                "CLOUD_KNOWLEDGE_TLS_SECRETS_DIR": str(tmp_path)})
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "--profile", "knowledge-cloud", "config", "--format", "json"],
                            cwd=root, env=env, capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    worker, proxy = services["worker-knowledge"], services["knowledge-embedding-tls"]
    gateway = services["cloud-gateway"]
    assert not worker.get("ports") and not proxy.get("ports")
    assert worker["environment"]["OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED"] == "false"
    assert gateway["environment"]["CLOUD_KNOWLEDGE_EMBEDDING_EXECUTION_ENABLED"] == "false"
    assert gateway["environment"]["CLOUD_KNOWLEDGE_EMBEDDING_ORIGIN"] == "https://knowledge-embedding-tls:8443"
    assert worker["depends_on"]["worker-knowledge-init"]["condition"] == "service_completed_successfully"
    assert proxy["depends_on"]["worker-knowledge"]["condition"] == "service_started"
    for service, target in ((worker, "/run/knowledge-cloud-worker"), (proxy, "/run/knowledge-embedding-tls")):
        assert any(item.get("target") == target and item.get("read_only") is True for item in service["volumes"])
    config = (root / "cloud/knowledge-embedding-tls/nginx.conf").read_text(encoding="utf-8")
    assert "ssl_verify_client on;" in config and "access_log off;" in config
    assert "location = /internal/cloud/v1/knowledge-embed/ready" in config
    assert "location = /internal/cloud/v1/knowledge-embed/execute" in config
    assert "location = /internal/v1/execute" not in config and "location / { return 404; }" in config


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_knowledge_index_profile_keeps_dependencies_private(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env["OPENCONNECT_WORKER_TOKEN"] = "compose-validation-token"
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "--profile", "knowledge-index", "config", "--format", "json"],
                            cwd=root, env=env, capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    consumer = services["worker-knowledge-index"]
    api = services["worker-knowledge-index-api"]
    expiry = services["worker-knowledge-expiry"]
    assert consumer["depends_on"]["worker-document"]["condition"] == "service_healthy"
    assert consumer["depends_on"]["minio"]["condition"] == "service_healthy"
    assert consumer["depends_on"]["worker-knowledge-init"]["condition"] == "service_completed_successfully"
    assert api["depends_on"]["worker-knowledge-index"]["condition"] == "service_started"
    assert consumer["environment"]["OC_INDEX_CLAM_SOCKET"] == "/run/clamav/clamd.sock"
    assert consumer["environment"]["OC_INDEX_DOCUMENT_SOCKET"] == "/run/openconnect/worker.sock"
    assert consumer["environment"]["OC_MODEL_OFFLINE"] == "true"
    for target in ("/models", "/run/clamav", "/run/openconnect"):
        assert any(volume.get("target") == target and volume.get("read_only") is True
                   for volume in consumer["volumes"])
    for service in (consumer, api, expiry, services["minio"], services["clamav"]):
        assert not service.get("ports")


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_minio_private_ca_is_mounted_read_only_for_both_clients(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env.update({"OPENCONNECT_WORKER_TOKEN": "compose-validation-token",
                "OC_MINIO_CA_DIRECTORY": str(tmp_path),
                "OC_INDEX_MINIO_CA_FILE": "/run/minio-ca/ca.pem",
                "OC_STORAGE_BOOTSTRAP_CA_FILE": "/run/minio-ca/ca.pem"})
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "--profile", "knowledge-index", "--profile", "storage-init",
                             "config", "--format", "json"], cwd=root, env=env,
                            capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    for name, variable in (("worker-knowledge-index", "OC_INDEX_MINIO_CA_FILE"),
                           ("storage-bucket-init", "OC_STORAGE_BOOTSTRAP_CA_FILE")):
        service = services[name]
        assert service["environment"][variable] == "/run/minio-ca/ca.pem"
        assert any(volume.get("target") == "/run/minio-ca" and volume.get("read_only") is True
                   for volume in service["volumes"])


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_optional_minio_tls_proxy_is_private_and_preserves_s3_host(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env.update({"OPENCONNECT_WORKER_TOKEN": "compose-validation-token", "OC_MINIO_TLS_DIRECTORY": str(tmp_path)})
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "--profile", "storage-tls", "config", "--format", "json"], cwd=root, env=env,
                            capture_output=True, check=True, timeout=30)
    proxy = json.loads(result.stdout)["services"]["minio-tls"]
    assert not proxy.get("ports") and proxy["read_only"] is True
    assert proxy["depends_on"]["minio"]["condition"] == "service_healthy"
    assert any(item.get("target") == "/run/minio-tls" and item.get("read_only") is True
               for item in proxy["volumes"])
    config = (root / "workers/knowledge/config/minio-tls.conf").read_text(encoding="utf-8")
    assert "proxy_set_header Host $http_host;" in config
    assert "access_log off;" in config and "ssl_protocols TLSv1.2 TLSv1.3;" in config


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_root_compose_passes_explicit_contributor_approval(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env["OPENCONNECT_WORKER_TOKEN"] = "compose-validation-token"
    env["OC_CONTRIBUTOR_DATA_USE_APPROVED"] = "1"
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "config", "--format", "json"], cwd=root, env=env,
                            capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    assert services["worker-inference"]["environment"]["OC_CONTRIBUTOR_DATA_USE_APPROVED"] == "1"


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_root_compose_declares_worker_and_storage_boundaries(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env = os.environ.copy()
    env["OPENCONNECT_WORKER_TOKEN"] = "compose-validation-token"
    env.pop("OC_MINIO_ROOT_USER", None)
    env.pop("OC_MINIO_ROOT_PASSWORD", None)
    env.pop("POSTGRES_PASSWORD", None)
    env.pop("OC_CONTRIBUTOR_DATA_USE_APPROVED", None)
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    command = ["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml"]
    for profile in ("documents", "vision", "vision-embed", "storage", "knowledge", "analytics", "classification",
                    "nllb", "mms-tts", "realtime-voice", "operational", "navigator"):
        command.extend(["--profile", profile])
    result = subprocess.run([*command, "config", "--format", "json"], cwd=root, env=env,
                            capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    assert services["worker-inference"]["environment"]["OC_CONTRIBUTOR_DATA_USE_APPROVED"] == "0"
    assert {"worker-document", "worker-document-init", "worker-vision", "worker-vision-init",
            "clamav", "minio", "worker-knowledge",
            "worker-analytics", "worker-classification", "worker-nllb", "worker-mms-tts",
            "worker-realtime-voice"}.issubset(services)
    document = services["worker-document"]
    assert document["network_mode"] == "none" and document["read_only"] is True
    assert "ALL" in document["cap_drop"] and document["pids_limit"] == 128
    assert int(document["mem_limit"]) == 2 * 1024**3
    assert "no-new-privileges:true" in document["security_opt"]
    assert "101" in [str(group) for group in document["group_add"]]
    assert document["depends_on"]["clamav"]["condition"] == "service_healthy"
    vision = services["worker-vision"]
    assert vision["network_mode"] == "none" and vision["read_only"] is True
    assert "ALL" in vision["cap_drop"] and vision["pids_limit"] == 128
    assert vision["depends_on"]["clamav"]["condition"] == "service_healthy"
    for name in ("worker-document", "worker-vision", "worker-vision-embed"):
        service = services[name]
        assert service["network_mode"] == "none"
        assert service["environment"]["OC_MODEL_OFFLINE"] == "true"
        assert service["environment"]["HF_HUB_OFFLINE"] == "1"
        assert service["environment"]["TRANSFORMERS_OFFLINE"] == "1"
        assert any(volume.get("target") == "/models" and volume.get("read_only") is True
                   for volume in service["volumes"])
    assert services["minio"]["environment"]["MINIO_ROOT_USER"] == ""
    assert services["minio"]["environment"]["MINIO_ROOT_PASSWORD"] == ""
    assert not services["minio"].get("ports") and not services["clamav"].get("ports")
    assert services["clamav"]["volumes"] and services["minio"]["volumes"]
    analytics = services["worker-analytics"]
    assert analytics["read_only"] is True and "ALL" in analytics["cap_drop"]
    assert analytics["pids_limit"] == 128 and int(analytics["mem_limit"]) == 512 * 1024**2
    assert "no-new-privileges:true" in analytics["security_opt"] and "healthcheck" in analytics
    assert not analytics.get("ports")
    password = services["postgres"]["environment"]["POSTGRES_PASSWORD"]
    for name, key in (("gateway-host", "ConnectionStrings__Default"),
                      ("todo-web-app", "ConnectionStrings__Default"),
                      ("worker-outbox", "WorkerHosts__Outbox__ConnectionString"),
                      ("worker-operational", "WorkerHosts__Operational__ConnectionString")):
        assert f"Password={password}" in services[name]["environment"][key]
    playwright = services["worker-playwright"]
    assert playwright["read_only"] is True and "ALL" in playwright["cap_drop"]
    assert "NAVIGATOR_WORKER_TOKEN" in playwright["environment"]


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_knowledge_sample_configuration_is_opt_in_and_forwarded(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env["OPENCONNECT_WORKER_TOKEN"] = "compose-validation-token"
    env["OC_TODO_KNOWLEDGE_DEMO_ENABLED"] = "true"
    env["OC_TODO_KNOWLEDGE_OBJECT_ID"] = "a" * 64
    env["OC_TODO_KNOWLEDGE_OBJECT_SHA256"] = "b" * 64
    env["OC_TODO_KNOWLEDGE_OBJECT_SIZE"] = "42"
    env["OC_TODO_KNOWLEDGE_OBJECT_VERSION"] = "fixture-version"
    env["OC_TODO_KNOWLEDGE_EMBEDDING_SPACE_ID"] = "c" * 64
    env.pop("OC_TODO_KNOWLEDGE_ALLOW_HTTP", None)
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "config", "--format", "json"], cwd=root, env=env,
                            capture_output=True, check=True, timeout=30)
    settings = json.loads(result.stdout)["services"]["todo-web-app"]["environment"]
    assert settings["OpenConnect__KnowledgeDemo__Enabled"] == "true"
    assert settings["OpenConnect__KnowledgeDemo__ObjectId"] == "a" * 64
    assert settings["OpenConnect__KnowledgeDemo__ObjectSha256"] == "b" * 64
    assert settings["OpenConnect__KnowledgeDemo__ObjectSize"] == "42"
    assert settings["OpenConnect__KnowledgeDemo__ObjectVersion"] == "fixture-version"
    assert settings["OpenConnect__KnowledgeDemo__EmbeddingSpaceId"] == "c" * 64
    assert settings["OpenConnect__KnowledgeDemo__AllowInsecureHttp"] == "false"


@pytest.mark.skipif(shutil.which("docker") is None, reason="Docker Compose is unavailable")
def test_cloud_privacy_reporting_requires_explicit_settings_and_trusted_certificate(tmp_path):
    root = Path(__file__).resolve().parents[3]
    env_file = tmp_path / "empty.env"
    env_file.write_text("", encoding="utf-8")
    env = os.environ.copy()
    env.update({
        "OPENCONNECT_WORKER_TOKEN": "compose-validation-token",
        "OC_TODO_CLOUD_PRIVACY_ENABLED": "true",
        "OC_TODO_CLOUD_PRIVACY_ORIGIN": "https://documentation-site:8443/",
        "OC_TODO_CLOUD_PRIVACY_CREDENTIAL": "fixture-credential",
        "OC_TODO_CLOUD_PRIVACY_SERVER_SHA256": "b" * 64,
        "CLOUD_DOCS_URLS": "http://+:8080;https://+:8443",
        "CLOUD_DOCS_TLS_CERTIFICATE_PATH": "/run/openconnect-docs-tls/server.pfx",
        "CLOUD_HOST_PRIVACY_TRUST_ENABLED": "true",
        "CLOUD_HOST_PRIVACY_CERTIFICATE_SHA256": "a" * 64,
    })
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "config", "--format", "json"], cwd=root, env=env,
                            capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    sample = services["todo-web-app"]["environment"]
    cloud = services["documentation-site"]
    assert sample["OpenConnect__CloudPrivacy__Enabled"] == "true"
    assert sample["OpenConnect__CloudPrivacy__Origin"] == "https://documentation-site:8443/"
    assert sample["OpenConnect__CloudPrivacy__Credential"] == "fixture-credential"
    assert sample["OpenConnect__CloudPrivacy__ServerCertificateSha256"] == "b" * 64
    assert cloud["environment"]["ASPNETCORE_URLS"] == "http://+:8080;https://+:8443"
    assert cloud["environment"]["ASPNETCORE_Kestrel__Certificates__Default__Path"] == "/run/openconnect-docs-tls/server.pfx"
    assert cloud["environment"]["OpenConnect__Cloud__HostPrivacyTrust__Enabled"] == "true"
    assert cloud["environment"]["OpenConnect__Cloud__HostPrivacyTrust__ClientCertificateSha256__0"] == "a" * 64
    assert any(item.get("target") == "/run/openconnect-docs-tls" and item.get("read_only") is True
               for item in cloud["volumes"])
    for name in ("OC_TODO_CLOUD_PRIVACY_ENABLED", "OC_TODO_CLOUD_PRIVACY_ORIGIN",
                 "OC_TODO_CLOUD_PRIVACY_CREDENTIAL", "CLOUD_HOST_PRIVACY_TRUST_ENABLED",
                 "CLOUD_HOST_PRIVACY_CERTIFICATE_SHA256"):
        env.pop(name, None)
    result = subprocess.run(["docker", "compose", "--env-file", str(env_file), "-f", "docker-compose-all.yml",
                             "config", "--format", "json"], cwd=root, env=env,
                            capture_output=True, check=True, timeout=30)
    services = json.loads(result.stdout)["services"]
    assert services["todo-web-app"]["environment"]["OpenConnect__CloudPrivacy__Enabled"] == "false"
    assert services["documentation-site"]["environment"]["OpenConnect__Cloud__HostPrivacyTrust__Enabled"] == "false"
