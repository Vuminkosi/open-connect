# OpenConnect.Metering

Usage metering and budgets for OpenConnect: model-usage records and budget guards
(`OpenConnect.Metering`) that cap spend per scope before work is performed.

## Install

```bash
dotnet add package OpenConnect.Metering
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Track consumption and enforce limits with the types in the `OpenConnect.Metering`
namespace, composed through `OpenConnect.Core` service registration. See the repository
docs (`docs/`) for budgeting semantics.

## License

Same terms as the OpenConnect repository.
