import json
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

from config import FIXTURES_DIR, SCHEMAS_DIR


@pytest.fixture(scope="session")
def schema_registry() -> Registry:
    """All eight schemas registered by $id.

    docs/data-contracts.md requires resolving relative $refs against local
    resources without fetching the network. A plain jsonschema.validate() would
    try to HTTP-GET cluck-in.example and hang.
    """
    resources = []
    for path in sorted(SCHEMAS_DIR.glob("*.schema.json")):
        contents = json.loads(path.read_text(encoding="utf-8"))
        resources.append((contents["$id"], Resource.from_contents(contents)))
    return Registry().with_resources(resources)


@pytest.fixture(scope="session")
def validator_for(schema_registry):
    def _make(schema_name: str) -> Draft202012Validator:
        schema = json.loads((SCHEMAS_DIR / schema_name).read_text(encoding="utf-8"))
        return Draft202012Validator(
            schema,
            registry=schema_registry,
            # Without an explicit format checker, "date-time" is only an
            # annotation and a naive timestamp would pass.
            format_checker=Draft202012Validator.FORMAT_CHECKER,
        )

    return _make


@pytest.fixture(scope="session")
def message_validator(validator_for):
    return validator_for("external-message.schema.json")


@pytest.fixture
def load_fixture():
    def _load(name: str) -> dict:
        return json.loads((FIXTURES_DIR / name).read_text(encoding="utf-8"))

    return _load
