# Knowledge model admission

The operator-controlled serving configuration must include `resourceRequest`
and `resourceBudget` using the shared worker admission contract. Set positive
`memoryMb` and `cpuMillicores` in the request to measured model requirements;
the budget's `memoryMbAvailable`, `vramMbAvailable`, and
`cpuMillicoresAvailable` must reflect the resources allocated to this process.
Startup remains unready if admission fails or if a request requires a GPU.

This adapter only implements CPU execution. `OC_MODEL_DEVICE=cpu` and `auto`
select the CPU adapter; `cuda` and unrecognized values remain unready. Resource
admission compares configured values; it does not measure host availability or
provide operating-system resource isolation. Deployments must enforce their
own limits and update budgets when allocations change.

## English MiniLM candidate

`openconnect_knowledge.profiles.minilm_configs` constructs matching init and
serving configurations for a pinned `sentence-transformers/all-MiniLM-L6-v2`
ONNX model and tokenizer. Supply a measured positive CPU and memory request,
the actual deployment budget, allowed artifact hosts, download timeout, and
disk reserve. The caller may serialize the returned Pydantic models with
`model_dump_json()` to its administrator-controlled configuration files.
Constructing the profile performs no network access and does not approve the
model, its license, deployment region, or suitability for an application.

The source may redirect model downloads to a CDN. Operators must review and
allow the actual destination host or provide a reviewed mirror; redirects to
unlisted or non-public hosts fail closed. The profile's 384-dimensional,
256-token mean-pooled output must not share an index with a different space ID.
