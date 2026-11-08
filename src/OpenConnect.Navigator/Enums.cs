using System.Text.Json.Serialization;
using OpenConnect.Abstractions;

namespace OpenConnect.Navigator;

[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorMode>))]
public enum NavigatorMode { Interactive, Operational, InteractiveAndOperational }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorBrowserProvider>))]
public enum NavigatorBrowserProvider { Disabled, PlaywrightMcp, Custom }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorHighlightProvider>))]
public enum NavigatorHighlightProvider { Native, Custom }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorSpeechProvider>))]
public enum NavigatorSpeechProvider { Disabled, Kokoro, MmsTts, Custom }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorSpeechFallback>))]
public enum NavigatorSpeechFallback { TextOnly, ExplicitAlternative }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorCaptureMode>))]
public enum NavigatorCaptureMode { None, Semantic, Element, MaskedScreenshot, WorkflowEvents }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorActionType>))]
public enum NavigatorActionType { Observe, Explain, Highlight, Focus, Scroll, Navigate, Click, Fill, Select, Toggle, InvokeRegisteredAction, Capture, Speak }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorActionRisk>))]
public enum NavigatorActionRisk { ReadOnly, Navigation, Interaction, Mutation, Sensitive, Forbidden }
[JsonConverter(typeof(KebabCaseStringEnumConverter<NavigatorSessionState>))]
public enum NavigatorSessionState { Created, Pairing, Ready, Running, AwaitingApproval, Paused, Completing, Completed, Failed, Cancelled, Expired }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SpeechModelProvisioning>))]
public enum SpeechModelProvisioning { Disabled, FirstStartIfMissing, VerifyExistingOnly }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SpeechCacheFailurePolicy>))]
public enum SpeechCacheFailurePolicy { FailWorker, ReprovisionApprovedManifest }

public enum NavigatorTourStepType { Explain, Navigate, Locate, Highlight, Focus, WaitFor, RequestApproval, Execute, Narrate, Capture, Complete }
