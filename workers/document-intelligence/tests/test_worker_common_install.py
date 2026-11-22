import importlib.util
from pathlib import Path


def test_shared_package_verifier_detects_changed_missing_and_extra_files(tmp_path):
    script = Path(__file__).resolve().parents[2] / "common" / "verify_install.py"
    spec = importlib.util.spec_from_file_location("verify_worker_common_install", script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    source, installed = tmp_path / "source", tmp_path / "installed"
    source.mkdir()
    installed.mkdir()
    assert module.differences(source, installed)
    (source / "a.py").write_text("current", encoding="utf-8")
    (installed / "a.py").write_text("current", encoding="utf-8")
    assert module.differences(source, installed) == []
    (source / "a.py").write_text("changed", encoding="utf-8")
    (source / "missing.py").write_text("new", encoding="utf-8")
    (installed / "extra.py").write_text("old", encoding="utf-8")
    assert module.differences(source, installed) == ["a.py", "extra.py", "missing.py"]
