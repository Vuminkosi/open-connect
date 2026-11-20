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
