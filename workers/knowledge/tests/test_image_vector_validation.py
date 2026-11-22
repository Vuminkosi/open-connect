from itertools import repeat

import pytest

from openconnect_knowledge.image_index import _vector


def test_vector_is_snapshotted_once_before_validation():
    class ChangingVector:
        iterations = 0

        def __iter__(self):
            self.iterations += 1
            return iter([1.0, 0.0] if self.iterations == 1 else [float("nan"), 0.0])

    vector = ChangingVector()
    assert _vector(vector) == [1.0, 0.0]
    assert vector.iterations == 1


def test_vector_snapshot_is_bounded_and_rejects_invalid_values():
    with pytest.raises(ValueError, match="invalid_vector"):
        _vector(repeat(0.0))
    with pytest.raises(ValueError, match="invalid_vector"):
        _vector([float("nan"), 0.0])
    with pytest.raises(ValueError, match="invalid_vector"):
        _vector([True, 0.0])
