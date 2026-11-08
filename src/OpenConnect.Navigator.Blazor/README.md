# OpenConnect Navigator — Blazor adapter

Optional client slice composing the guided-tour bridge with the existing
Blazor shell. It never replaces `MiniComposer` and never creates a duplicate
root provider: nest `NavigatorProvider` inside (or outside)
`OpenConnectProvider` and keep `MiniComposer` as a sibling.

## Register

```csharp
// Program.cs — alongside the existing composer registration.
builder.Services.AddOpenConnectComponents();   // existing: provider + MiniComposer
builder.Services.AddOpenConnectNavigator();    // this adapter: scoped tour state
```

## Compose (coexists with MiniComposer — NAV-AT-007)

```razor
@using OpenConnect.Components.Blazor
@using OpenConnect.Navigator.Blazor

<OpenConnectProvider SessionId="@sessionId">
    <NavigatorProvider RouteId="checkout">
        <MiniComposer AssistantName="Assistant" />
        <NavigatorTargetAnchor TargetId="pay-now" Label="Pay now">
            <button @onclick="Pay">Pay now</button>
        </NavigatorTargetAnchor>
        @Body
    </NavigatorProvider>
</OpenConnectProvider>
```

Start a tour from any injected context (button handler, onboarding step):

```razor
@inject NavigatorTourService Tour

<button @onclick="Start">Take the tour</button>

@code {
    private void Start() => Tour.StartTour(
        [new NavigatorTourStep("Pay", "Choose pay to finish.", "pay-now")],
        "checkout");
}
```

No `MiniComposer` registration changes are required. `NavigatorProvider`
only cascades the tour service and renders the highlight/overlay siblings.

## SSR / hydration (Blazor Server, WASM, static SSR)

- No `IJSRuntime` dependency anywhere in this project. Components render as
  static text-first markup during prerendering; interactivity (focus,
  Escape-to-pause) activates only under an interactive render mode.
- `NavigatorTourService` is DOM-free: registry, validation, and the text
  tour (`RenderTextTour`) work on server and client.
- `ElementReference.FocusAsync` is best-effort inside `try/catch`, so static
  SSR never throws.
- `NavigatorProvider` stops the tour and (by default) unregisters the
  route's targets on unmount/logout. Set `DisposeOnUnmount="false"` when the
  registry lifetime is owned elsewhere.

## Accessibility

- Overlay is `role="dialog"` with an `aria-label`, progress caption
  ("Step N of M"), single primary action per step, Back/Next/Pause/Skip
  controls with `aria-label`s, and a polite live-region announcer.
- Focus moves into the card per step; Escape pauses (keeps progress) rather
  than destroying it.
- `prefers-reduced-motion` disables transitions; `prefers-color-scheme`
  drives the dark theme; logical CSS properties keep RTL correct.
- When speech is absent (always — this adapter ships no TTS), the text tour
  and overlay copy are the experience, not a degraded path.

## What this slice does NOT do

- No real DOM anchoring/positioning (no JS). The highlight ring is a visual
  anchor hook on `[data-navigator-target]`; host JS may progressively enhance
  positioning later. Nothing fakes a position.
- No server grant/probe logic — route navigation still needs host
  authorization per the existing navigator contract.
