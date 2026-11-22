from pathlib import Path


def test_migration_files_are_included_in_knowledge_image_build_context():
    root = Path(__file__).resolve().parents[1]
    dockerfile = (root / "Dockerfile").read_text(encoding="utf-8")
    rules = (root / "Dockerfile.dockerignore").read_text(encoding="utf-8").splitlines()
    assert rules[0] == "**"
    assert "!workers/knowledge/migrations" in rules
    assert "!workers/knowledge/migrations/**" in rules
    assert "COPY workers/knowledge/migrations/ ./migrations/" in dockerfile
    assert (root / "migrations" / "0001_knowledge_index.sql").is_file()


def test_worker_images_install_shared_source_without_uv_cache():
    workers = Path(__file__).resolve().parents[2]
    for worker in ("knowledge", "vision", "document-intelligence", "analytics-inference", "inference"):
        dockerfile = (workers / worker / "Dockerfile").read_text(encoding="utf-8")
        assert "COPY workers/common/src/ /app/workers/common/src/" in dockerfile
        sync = next(line for line in dockerfile.splitlines() if line.startswith("RUN uv sync "))
        assert "--locked" in sync and "--no-cache" in sync and "--no-install-project" in sync
        assert "COPY workers/common/verify_install.py /app/workers/common/verify_install.py" in dockerfile
        assert ".venv/bin/python ../common/verify_install.py" in sync
        rules = (workers / worker / "Dockerfile.dockerignore").read_text(encoding="utf-8").splitlines()
        assert "!workers/common/verify_install.py" in rules
