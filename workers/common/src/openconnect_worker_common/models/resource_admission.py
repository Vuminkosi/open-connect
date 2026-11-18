"""Resource admission: admit model loads only when host budgets allow."""

from enum import StrEnum

from pydantic import BaseModel, ConfigDict, Field


class AdmissionVerdict(StrEnum):
    """Admission outcomes; wire values are kebab-case."""

    ADMIT = "admit"
    DENY = "deny"


class ResourceRequest(BaseModel):
    """Resources a model load asks for (mebibytes / millicores)."""

    model_config = ConfigDict(extra="forbid")

    memoryMb: int = Field(default=0, ge=0)
    vramMb: int = Field(default=0, ge=0)
    cpuMillicores: int = Field(default=0, ge=0)
    gpuRequired: bool = False


class ResourceBudget(BaseModel):
    """Currently available host resources."""

    model_config = ConfigDict(extra="forbid")

    memoryMbAvailable: int = Field(ge=0)
    vramMbAvailable: int = Field(ge=0)
    cpuMillicoresAvailable: int = Field(ge=0)
    gpuAvailable: bool = False


class AdmissionDecision(BaseModel):
    """Admission outcome with a human-readable, PII-free reason."""

    model_config = ConfigDict(extra="forbid")

    verdict: AdmissionVerdict
    reason: str = Field(min_length=1, max_length=512)


def admit(request: ResourceRequest, budget: ResourceBudget) -> AdmissionDecision:
    """Decide admission with pure arithmetic. Denials name the limiting resource."""
    if request.gpuRequired and not budget.gpuAvailable:
        return AdmissionDecision(verdict=AdmissionVerdict.DENY, reason="no GPU available")
    if request.memoryMb > budget.memoryMbAvailable:
        return AdmissionDecision(
            verdict=AdmissionVerdict.DENY,
            reason="insufficient host memory for requested model",
        )
    if request.vramMb > budget.vramMbAvailable:
        return AdmissionDecision(
            verdict=AdmissionVerdict.DENY,
            reason="insufficient device memory for requested model",
        )
    if request.cpuMillicores > budget.cpuMillicoresAvailable:
        return AdmissionDecision(
            verdict=AdmissionVerdict.DENY,
            reason="insufficient CPU budget for requested model",
        )
    return AdmissionDecision(verdict=AdmissionVerdict.ADMIT, reason="within budget")
