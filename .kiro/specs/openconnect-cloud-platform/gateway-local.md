# Cloud gateway local process

The root Compose stack provides a separate Cloud gateway on a loopback-bound port. The local Node adapter runs the same request handler shape used by a Cloudflare Worker. The process provides liveness and denies all other requests with a service-unavailable response until authentication, authorization, quota reservation, and private worker routing are implemented. It does not provide a ready data plane or establish Cloudflare deployment compatibility.
