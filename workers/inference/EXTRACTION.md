# Structured extraction adapter

The extraction module validates a bounded, flat JSON Schema and requires each
non-null value to cite an exact source span. A local llama.cpp adapter is
available for an already provisioned and authorized model. It does not load or
download a model or authorize a tenant by itself.

The inference worker provides a private `POST /internal/v1/extract` route for
`documents.extract`. It requires the configured worker bearer token and an
HMAC signature over the exact JSON body using `OPENCONNECT_WORKER_SIGNING_KEY`.
The trusted host must authenticate and authorize the tenant, content, provider
and privacy policy before signing; worker credentials do not establish end-user
authorization. Request and response contracts are in `contracts/jsonschema`.

The route is disabled by default. Setting `OC_EXTRACTION_ENABLED=true` alone is
not enough: the host must install an `ExtractionRuntime` or configure
`OC_EXTRACTION_CONFIG_PATH` with an approved `ExtractionConfig`. The startup
loader checks admission, artifact review, license and the shared verified cache
before loading; it rechecks the selected fingerprint on every request and never
provisions a model itself. The shared model initializer can populate the cache
in a separate initialization stage. In
production the registered provider must wrap a real `llama_cpp.Llama` model;
fake providers fail readiness. The host must coordinate concurrent access if
the same model is used by other routes. The inference image includes the CPU
llama.cpp runtime; the loader remains unready when approved assets are missing.
The root Compose `extraction` profile runs the initializer with
`OC_EXTRACTION_INIT_CONFIG`; the serving worker uses
`OC_EXTRACTION_SERVE_CONFIG` and mounts the model cache read-only. Both paths
default to an unconfigured placeholder and require deployment-owned approved
configuration. Enable the serving capability explicitly with
`OC_EXTRACTION_ENABLED=true`.

`qwen_configs` in `extraction_profiles` creates matching initializer and serving
configurations for the pinned Qwen3-0.6B Q4_K_M candidate. The factory requires
an explicit reviewed converted-artifact license, operator resource budgets, download hosts
and timeout. Creating the configuration performs no download. Review the
converted artifact's terms for the intended deployment before approving it;
the source model's license alone does not establish that review.
The initializer's allowlist must include `huggingface.co` and every approved
redirect host. A source may change its CDN host; missing approval fails the
download rather than silently extending the allowlist.
No provider is advertised when it is missing, unverified or disabled.

Before a provider is called, recognizable instruction-like lines are masked
while preserving character offsets. The result reports
`instruction_like_line_excluded` when this occurs. This heuristic can miss
instructions and can exclude legitimate content; it is not a general
prompt-injection defense. All extracted fields require application review.
Source citations show literal support, not semantic correctness.

For numeric fields, the local adapter accepts a small allowlist of units after
the literal number and cites the number only. It does not convert units. If
numeric `subtotal`, `tax`, and `total` fields are present and subtotal plus tax
differs from total, the result includes `subtotal_plus_tax_differs_from_total`.
This is advisory: discounts, shipping, rounding or other adjustments may
explain the difference.

The optional real-model acceptance test requires an operator-provided model
and runs only with `OC_RUN_EXTRACTION_MODEL_TESTS=1` and
`OC_EXTRACTION_MODEL_PATH` configured. A small passing fixture does not prove
general extraction quality or safety.
