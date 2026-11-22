from pathlib import Path

import pytest

from openconnect_knowledge import migrate as migration


def test_repository_migrations_are_contiguous_and_wrapped():
    directory = Path(__file__).resolve().parents[1] / "migrations"
    items = migration.load_migrations(directory)
    assert len(items) >= 6
    assert items[0][0] == "0001_knowledge_index.sql"
    assert all(len(digest) == 64 and body.strip() for _, digest, body in items)


@pytest.mark.parametrize("contents", ["SELECT 1;", "BEGIN; SELECT 1; ROLLBACK; COMMIT;",
                                       "BEGIN; SELECT 1; COMMIT; SELECT 2;"])
def test_rejects_unwrapped_or_nested_transactions(tmp_path, contents):
    (tmp_path / "0001_test.sql").write_text(contents, encoding="utf-8")
    with pytest.raises(ValueError, match="invalid_migration_transaction"):
        migration.load_migrations(tmp_path)


def test_rejects_gapped_migration_sequence(tmp_path):
    (tmp_path / "0002_test.sql").write_text("BEGIN; SELECT 1; COMMIT;", encoding="utf-8")
    with pytest.raises(ValueError, match="invalid_migration_sequence"):
        migration.load_migrations(tmp_path)


class Result:
    def __init__(self, result):
        self.result = result

    def fetchone(self):
        return self.result

    def fetchall(self):
        return self.result


class Connection:
    def __init__(self, tracked, schema, rows):
        self.tracked, self.schema, self.rows = tracked, schema, rows
        self.commands = []

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def execute(self, sql, params=None):
        self.commands.append((sql, params))
        if "to_regclass" in sql:
            return Result((self.tracked, self.schema))
        if sql.startswith("SELECT name, sha256"):
            return Result(self.rows)
        return Result(None)


def test_missing_required_relation_prevents_success(monkeypatch, tmp_path):
    (tmp_path / "0001_test.sql").write_text("BEGIN; SELECT 1; COMMIT;", encoding="utf-8")

    class MissingRelation(Connection):
        def execute(self, sql, params=None):
            if "FROM openconnect_knowledge.outbox LIMIT 0" in sql:
                raise RuntimeError("missing relation")
            return super().execute(sql, params)

    connection = MissingRelation(None, None, [])
    monkeypatch.setattr(migration.psycopg, "connect", lambda *args, **kwargs: connection)
    with pytest.raises(RuntimeError, match="missing relation"):
        migration.migrate("database", tmp_path)


def test_rejects_untracked_schema_before_applying_any_migration(monkeypatch, tmp_path):
    (tmp_path / "0001_test.sql").write_text("BEGIN; SELECT 1; COMMIT;", encoding="utf-8")
    connection = Connection(None, "openconnect_knowledge", [])
    monkeypatch.setattr(migration.psycopg, "connect", lambda *args, **kwargs: connection)
    with pytest.raises(RuntimeError, match="untracked_existing_schema"):
        migration.migrate("database", tmp_path)
    assert not any("CREATE TABLE" in sql for sql, _ in connection.commands)


def test_applies_pending_sequence_and_rejects_changed_history(monkeypatch, tmp_path):
    (tmp_path / "0001_test.sql").write_text("BEGIN; SELECT 1; COMMIT;", encoding="utf-8")
    connection = Connection(None, None, [])
    monkeypatch.setattr(migration.psycopg, "connect", lambda *args, **kwargs: connection)
    assert migration.migrate("database", tmp_path) == 1
    assert any("SELECT 1" in sql for sql, _ in connection.commands)
    connection = Connection("journal", "openconnect_knowledge", [("0001_test.sql", "wrong")])
    monkeypatch.setattr(migration.psycopg, "connect", lambda *args, **kwargs: connection)
    with pytest.raises(RuntimeError, match="migration_history_mismatch"):
        migration.migrate("database", tmp_path)
