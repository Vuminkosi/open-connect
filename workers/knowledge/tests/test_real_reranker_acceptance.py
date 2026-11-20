"""Opt-in runtime download and offline ranking smoke test."""

import os
import socket

import pytest

from openconnect_knowledge.rerank_profile import reranker_config
from openconnect_knowledge.reranker import Candidate, OnnxReranker
from openconnect_worker_common.models.initialize import initialize
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager

pytestmark = pytest.mark.skipif(os.environ.get("OC_RUN_REAL_RERANKER_TESTS") != "1",
                                reason="real model download requires explicit opt-in")


def test_real_model_first_start_and_offline_restart(tmp_path, monkeypatch):
    config = reranker_config(ResourceRequest(memoryMb=512, cpuMillicores=500),
        ResourceBudget(memoryMbAvailable=1024, vramMbAvailable=0, cpuMillicoresAvailable=1000),
        allowed_hosts=["huggingface.co", "us.aws.cdn.hf.co", "cas-bridge.xethub.hf.co"],
        timeout_seconds=120, disk_reserve_bytes=0)
    ready = initialize(config, tmp_path, provisioning="first-start-if-missing", offline=False)
    assert ready.ready and ready.downloads_attempted == 2

    def deny(*args, **kwargs):
        raise AssertionError("network forbidden after initialization")

    monkeypatch.setattr(socket, "getaddrinfo", deny)
    monkeypatch.setattr(socket, "create_connection", deny)
    reused = initialize(config, tmp_path, provisioning="first-start-if-missing", offline=True)
    assert reused.ready and reused.cache_hit and reused.downloads_attempted == 0
    engine = OnnxReranker(RuntimeModelCacheManager(tmp_path, "knowledge"), config.manifest,
                          model_asset="model.onnx", tokenizer_asset="tokenizer.json")
    ranking = engine.rank("Which planet is known as the Red Planet?", [
        Candidate("irrelevant", "Jupiter is a gas giant."),
        Candidate("relevant", "Mars is known as the Red Planet because of its red color."),
    ])
    assert ranking.candidates[0].id == "relevant" and ranking.score_type == "raw_logit"
