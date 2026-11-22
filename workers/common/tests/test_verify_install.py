import importlib.util
from pathlib import Path


spec = importlib.util.spec_from_file_location("verify_install", Path(__file__).parents[1] / "verify_install.py")
assert spec and spec.loader
verify_install = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify_install)


def test_detects_missing_extra_and_changed_python_sources(tmp_path):
    source, installed = tmp_path / "source", tmp_path / "installed"
    source.mkdir()
    installed.mkdir()
    (source / "equal.py").write_text("same", encoding="utf-8")
    (installed / "equal.py").write_text("same", encoding="utf-8")
    assert verify_install.differences(source, installed) == []
    (source / "changed.py").write_text("source", encoding="utf-8")
    (installed / "changed.py").write_text("installed", encoding="utf-8")
    (source / "missing.py").write_text("source", encoding="utf-8")
    (installed / "extra.py").write_text("installed", encoding="utf-8")
    assert verify_install.differences(source, installed) == ["changed.py", "extra.py", "missing.py"]


def test_empty_package_fails_closed(tmp_path):
    assert verify_install.differences(tmp_path / "missing", tmp_path / "empty") == [
        "package source or installation is empty"
    ]
