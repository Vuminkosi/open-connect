using System.Buffers.Binary;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;
using OpenConnect.Core.Speech;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class VoiceDuplexTests
{
    private static DuplexVoiceSessionCoordinator CreateCoordinator(Action<OpenConnectVoiceOptions>? configure = null)
    {
        var opts = new OpenConnectVoiceOptions();
        configure?.Invoke(opts);
        return new DuplexVoiceSessionCoordinator(Options.Create(opts));
    }

    [Fact]
    public void OpenConnectVoiceOptions_DuplexDefaultsPreserveV1Behavior()
    {
        var opts = new OpenConnectVoiceOptions();

        Assert.True(opts.EnableLiveTranscription);
        Assert.True(opts.EnableRecordedTranscription);
        Assert.True(opts.EnableSpeechOutput);
        Assert.False(opts.EnableContinuousConversation);
        Assert.False(opts.EnableStreamingSpeechOutput);
        Assert.False(opts.EnableSemanticTurnDetection);
        Assert.Equal(32L * 1024 * 1024, opts.MaxUtteranceAudioBytes);
        Assert.Equal(256L * 1024 * 1024, opts.MaxRollingSessionAudioBytes);
        Assert.Equal(3600, opts.RollingSessionWindowSeconds);
        Assert.Equal(256, opts.MaxBufferedOutputFrames);
        Assert.Equal(350, opts.PhraseFlushTimeoutMs);
        Assert.Equal(8, opts.MinPhraseWords);
        Assert.Equal(25, opts.MaxPhraseWords);
        Assert.False(opts.ConservativeEndpointing);
    }

    [Theory]
    [InlineData(VoiceAudioStreamKind.MicInput, 1u, 1L, 0L, 0L, 960)]
    [InlineData(VoiceAudioStreamKind.TtsOutput, 42u, 7L, 128L, 48_000L, 1920)]
    public void VoiceBinaryFrameCodec_RoundTripsValidFrames(
        VoiceAudioStreamKind kind,
        uint streamId,
        long epoch,
        long sequence,
        long sampleOffset,
        int payloadBytes)
    {
        var header = new VoiceAudioFrameHeader(kind, streamId, epoch, sequence, sampleOffset);
        var payload = new byte[payloadBytes];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        var frame = new byte[VoiceBinaryFrameCodec.HeaderSize + payloadBytes];
        Assert.True(VoiceBinaryFrameCodec.TryEncode(header, payload, frame, out var written));
        Assert.Equal(frame.Length, written);
        Assert.Equal(VoiceBinaryFrameCodec.MagicByte0, frame[0]);
        Assert.Equal(VoiceBinaryFrameCodec.MagicByte1, frame[1]);
        Assert.Equal(VoiceBinaryFrameCodec.ProtocolVersion, frame[2]);
        Assert.Equal((byte)kind, frame[3]);
        Assert.Equal(streamId, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)));
        Assert.Equal(epoch, BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(8, 8)));
        Assert.Equal(sequence, BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(16, 8)));
        Assert.Equal(sampleOffset, BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(24, 8)));

        Assert.True(VoiceBinaryFrameCodec.TryDecode(frame, out var decodedHeader, out var decodedPayload));
        Assert.Equal(header, decodedHeader);
        Assert.True(payload.AsSpan().SequenceEqual(decodedPayload));
    }

    [Fact]
    public void VoiceBinaryFrameCodec_RejectsMalformedHeadersOddPayloadAndOverflow()
    {
        var validHeader = new VoiceAudioFrameHeader(VoiceAudioStreamKind.MicInput, 1, 1, 0, 0);
        var validFrame = new byte[VoiceBinaryFrameCodec.HeaderSize + 4];
        Assert.True(VoiceBinaryFrameCodec.TryEncode(validHeader, new byte[4], validFrame, out _));

        // Bad magic
        var badMagic = validFrame.ToArray();
        badMagic[0] = 0x00;
        Assert.False(VoiceBinaryFrameCodec.TryDecode(badMagic, out _, out _));

        // Bad version
        var badVersion = validFrame.ToArray();
        badVersion[2] = 1;
        Assert.False(VoiceBinaryFrameCodec.TryDecode(badVersion, out _, out _));

        // Bad kind
        var badKind = validFrame.ToArray();
        badKind[3] = 9;
        Assert.False(VoiceBinaryFrameCodec.TryDecode(badKind, out _, out _));

        // Truncated header
        Assert.False(VoiceBinaryFrameCodec.TryDecode(new byte[31], out _, out _));

        // Odd PCM payload byte length
        var oddFrame = new byte[VoiceBinaryFrameCodec.HeaderSize + 3];
        validFrame.AsSpan(0, 32).CopyTo(oddFrame);
        Assert.False(VoiceBinaryFrameCodec.TryDecode(oddFrame, out _, out _));
        Assert.False(VoiceBinaryFrameCodec.TryEncode(validHeader, new byte[3], new byte[64], out _));

        // Overflow (> 96 KB)
        var oversized = new byte[VoiceBinaryFrameCodec.MaxFrameBytes + 2];
        validFrame.AsSpan(0, 32).CopyTo(oversized);
        Assert.False(VoiceBinaryFrameCodec.TryDecode(oversized, out _, out _));
        Assert.False(VoiceBinaryFrameCodec.TryEncode(
            validHeader,
            new byte[VoiceBinaryFrameCodec.MaxPayloadBytes + 2],
            new byte[VoiceBinaryFrameCodec.MaxFrameBytes + 2],
            out _));

        // Negative epoch
        var negEpoch = new VoiceAudioFrameHeader(VoiceAudioStreamKind.MicInput, 1, -1, 0, 0);
        Assert.False(VoiceBinaryFrameCodec.TryEncode(negEpoch, new byte[4], new byte[64], out _));
    }

    [Fact]
    public void DuplexCoordinator_IsolatesCompositeTenantPrincipalSessionKeys()
    {
        var coordinator = CreateCoordinator();
        var tenantA = new TenantId(Ulid.NewUlid());
        var tenantB = new TenantId(Ulid.NewUlid());
        var principalA = new PrincipalId(Ulid.NewUlid());
        var sharedSession = new SessionId(Ulid.NewUlid());

        var snapA = coordinator.OpenSession(tenantA, principalA, sharedSession, "vc-a");
        Assert.Equal(1, snapA.SpeechEpoch);

        // Different tenant with same SessionId must not see tenantA's session.
        Assert.Null(coordinator.GetSnapshot(tenantB, principalA, sharedSession));
        var crossInterrupt = coordinator.HandleInterrupt(
            tenantB,
            principalA,
            sharedSession,
            new VoiceInterruptRequest("req-cross", null, 1, 0));
        Assert.False(crossInterrupt.Accepted);
        Assert.Equal(ErrorCodes.SessionNotFound, crossInterrupt.RejectionReason);

        // Tenant A's epoch remains unaffected.
        Assert.Equal(1, coordinator.GetSnapshot(tenantA, principalA, sharedSession)!.SpeechEpoch);
    }

    [Fact]
    public void DuplexCoordinator_EnforcesPartialRevisionMonotonicityAndFinalIdempotency()
    {
        var coordinator = CreateCoordinator();
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        coordinator.OpenSession(tenant, principal, session, "vc-1");
        var capture = coordinator.BeginCapture(tenant, principal, session, "cap-1", "vc-1");
        Assert.True(capture.Accepted);
        Assert.Equal(1, capture.SpeechEpoch);

        var p1 = coordinator.RecordPartial(tenant, principal, session, "cap-1", 1, 1, "Show my");
        Assert.True(p1.Accepted);
        Assert.Equal(1, p1.Revision);

        var pStale = coordinator.RecordPartial(tenant, principal, session, "cap-1", 1, 1, "Show my");
        Assert.False(pStale.Accepted);
        Assert.Equal("stale_partial_revision", pStale.RejectionReason);

        var p2 = coordinator.RecordPartial(tenant, principal, session, "cap-1", 1, 2, "Show my tasks");
        Assert.True(p2.Accepted);

        var final1 = coordinator.TryAdmitFinal(tenant, principal, session, "cap-1", 1, "Show my tasks.");
        Assert.True(final1.Admitted);
        Assert.False(final1.IsDuplicate);
        Assert.Equal("voice:cap-1:1", final1.IdempotencyKey);

        var finalDup = coordinator.TryAdmitFinal(tenant, principal, session, "cap-1", 1, "Show my tasks.");
        Assert.False(finalDup.Admitted);
        Assert.True(finalDup.IsDuplicate);
        Assert.Equal(final1.TurnId, finalDup.TurnId);
    }

    [Fact]
    public void DuplexCoordinator_HandlesInterruptBeforeFirstAudioAndIdempotentDuplicateInterrupt()
    {
        var coordinator = CreateCoordinator();
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        coordinator.OpenSession(tenant, principal, session, "vc-1");
        Assert.True(coordinator.StartResponse(tenant, principal, session, "resp-1", 1).Accepted);

        var interrupt1 = coordinator.HandleInterrupt(
            tenant,
            principal,
            session,
            new VoiceInterruptRequest("int-1", "resp-1", 1, 0, "barge_in"));
        Assert.True(interrupt1.Accepted);
        Assert.False(interrupt1.WasIdempotentReplay);
        Assert.Equal(1, interrupt1.PreviousSpeechEpoch);
        Assert.Equal(2, interrupt1.NewSpeechEpoch);
        Assert.NotNull(interrupt1.Reconciliation);
        Assert.Empty(interrupt1.Reconciliation!.ConfirmedPlayedSegments);
        Assert.Null(interrupt1.Reconciliation.UncertainPartiallyPlayedSegment);
        Assert.Empty(interrupt1.Reconciliation.UnheardSegments);

        // Duplicate interrupt with same RequestId is idempotent and does NOT advance epoch again.
        var interruptDup = coordinator.HandleInterrupt(
            tenant,
            principal,
            session,
            new VoiceInterruptRequest("int-1", "resp-1", 1, 0, "barge_in"));
        Assert.True(interruptDup.Accepted);
        Assert.True(interruptDup.WasIdempotentReplay);
        Assert.Equal(2, interruptDup.NewSpeechEpoch);
        Assert.Equal(2, coordinator.GetSnapshot(tenant, principal, session)!.SpeechEpoch);

        // Late segment for old epoch is rejected.
        var lateSeg = coordinator.RegisterAudioSegment(
            tenant,
            principal,
            session,
            "resp-1",
            1,
            new VoiceSegmentMetadata("seg-0", 0, "Hello", "Hello", 0, 12_000));
        Assert.False(lateSeg.Accepted);
        Assert.Equal("stale_speech_epoch", lateSeg.RejectionReason);
    }

    [Fact]
    public void DuplexCoordinator_ReconcilesConfirmedUncertainAndUnheardWithoutFabricatingWordBoundaries()
    {
        var coordinator = CreateCoordinator();
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        coordinator.OpenSession(tenant, principal, session, "vc-1");
        Assert.True(coordinator.StartResponse(tenant, principal, session, "resp-2", 1).Accepted);

        var seg0 = new VoiceSegmentMetadata("seg-0", 0, "First sentence.", "First sentence.", 0, 24_000);
        var seg1 = new VoiceSegmentMetadata("seg-1", 1, "Second sentence here.", "Second sentence here.", 24_000, 24_000);
        var seg2 = new VoiceSegmentMetadata("seg-2", 2, "Third sentence.", "Third sentence.", 48_000, 24_000);

        Assert.True(coordinator.RegisterAudioSegment(tenant, principal, session, "resp-2", 1, seg0).Accepted);
        Assert.True(coordinator.RegisterAudioSegment(tenant, principal, session, "resp-2", 1, seg1).Accepted);
        Assert.True(coordinator.RegisterAudioSegment(tenant, principal, session, "resp-2", 1, seg2).Accepted);

        // Generation completes before playback drains.
        var draining = coordinator.CompleteResponseGeneration(tenant, principal, session, "resp-2", 1);
        Assert.Equal(VoiceReasoningState.Completed, draining!.ReasoningState);
        Assert.Equal(VoiceOutputState.Draining, draining.OutputState);

        // Reject impossible receipt > transmitted samples (72,000) and wrong responseId.
        var badHigh = coordinator.RecordPlaybackReceipt(tenant, principal, session, new VoicePlaybackReceipt("resp-2", 1, 90_000));
        Assert.False(badHigh.Accepted);
        Assert.Equal("invalid_playback_offset", badHigh.RejectionReason);

        var badResp = coordinator.RecordPlaybackReceipt(tenant, principal, session, new VoicePlaybackReceipt("resp-other", 1, 10_000));
        Assert.False(badResp.Accepted);
        Assert.Equal("response_mismatch", badResp.RejectionReason);

        // Valid receipt partway through seg-1 (36,000 samples).
        var okReceipt = coordinator.RecordPlaybackReceipt(tenant, principal, session, new VoicePlaybackReceipt("resp-2", 1, 36_000));
        Assert.True(okReceipt.Accepted);

        // Reject non-monotonic regression.
        var regressed = coordinator.RecordPlaybackReceipt(tenant, principal, session, new VoicePlaybackReceipt("resp-2", 1, 20_000));
        Assert.False(regressed.Accepted);
        Assert.Equal("non_monotonic_playback_offset", regressed.RejectionReason);

        // Interrupt while draining at 36,000 samples.
        var interrupt = coordinator.HandleInterrupt(
            tenant,
            principal,
            session,
            new VoiceInterruptRequest("int-drain", "resp-2", 1, 36_000));
        Assert.True(interrupt.Accepted);
        var rec = interrupt.Reconciliation!;
        Assert.True(rec.ReceiptReceived);
        Assert.Single(rec.ConfirmedPlayedSegments);
        Assert.Equal("seg-0", rec.ConfirmedPlayedSegments[0].SegmentId);
        Assert.Equal("First sentence.", rec.ConfirmedText);
        Assert.NotNull(rec.UncertainPartiallyPlayedSegment);
        Assert.Equal("seg-1", rec.UncertainPartiallyPlayedSegment!.SegmentId);
        Assert.Equal("Second sentence here.", rec.UncertainText);
        Assert.Single(rec.UnheardSegments);
        Assert.Equal("seg-2", rec.UnheardSegments[0].SegmentId);
    }

    [Fact]
    public void DuplexCoordinator_FenceConnection_MarksDeliveryUncertainAndAdvancesEpoch()
    {
        var coordinator = CreateCoordinator();
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        coordinator.OpenSession(tenant, principal, session, "vc-old");
        Assert.True(coordinator.StartResponse(tenant, principal, session, "resp-disc", 1).Accepted);
        Assert.True(coordinator.RegisterAudioSegment(
            tenant,
            principal,
            session,
            "resp-disc",
            1,
            new VoiceSegmentMetadata("seg-0", 0, "Unreceipted audio.", "Unreceipted audio.", 0, 24_000)).Accepted);

        var fenced = coordinator.FenceConnection(tenant, principal, session, "vc-old", markReconnecting: true);
        Assert.True(fenced.Fenced);
        Assert.Equal(2, fenced.SpeechEpoch);
        Assert.Equal(VoiceConnectionState.Reconnecting, fenced.ConnectionState);
        Assert.NotNull(fenced.Reconciliation);
        Assert.False(fenced.Reconciliation!.ReceiptReceived);
        Assert.Empty(fenced.Reconciliation.ConfirmedPlayedSegments);
        Assert.Equal("seg-0", fenced.Reconciliation.UncertainPartiallyPlayedSegment!.SegmentId);

        // Stale connection cannot fence again after reconnect.
        coordinator.OpenSession(tenant, principal, session, "vc-new");
        var staleFence = coordinator.FenceConnection(tenant, principal, session, "vc-old");
        Assert.False(staleFence.Fenced);
        Assert.Equal("stale_voice_connection", staleFence.RejectionReason);
    }

    [Fact]
    public async Task DuplexCoordinator_ConcurrentInterruptAndReceiptRace_MaintainsMonotonicEpoch()
    {
        var coordinator = CreateCoordinator();
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        coordinator.OpenSession(tenant, principal, session, "vc-race");
        Assert.True(coordinator.StartResponse(tenant, principal, session, "resp-race", 1).Accepted);
        Assert.True(coordinator.RegisterAudioSegment(
            tenant,
            principal,
            session,
            "resp-race",
            1,
            new VoiceSegmentMetadata("seg-0", 0, "Racing output.", "Racing output.", 0, 24_000)).Accepted);

        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
            {
                coordinator.HandleInterrupt(
                    tenant,
                    principal,
                    session,
                    new VoiceInterruptRequest($"race-req-{i % 4}", "resp-race", 1, 12_000));
            }
            else
            {
                coordinator.RecordPlaybackReceipt(
                    tenant,
                    principal,
                    session,
                    new VoicePlaybackReceipt("resp-race", 1, 12_000));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Exactly one epoch-1 interrupt can win; all others either replay idempotently or see stale_speech_epoch.
        var snapshot = coordinator.GetSnapshot(tenant, principal, session)!;
        Assert.Equal(2, snapshot.SpeechEpoch);
        Assert.Equal(VoiceOutputState.Interrupted, snapshot.OutputState);
    }

    [Fact]
    public void SpeechPhraseFormatter_StripsMarkdownCodeAndUrlsWithoutSplittingDecimalsAbbreviationsOrSurrogates()
    {
        var formatter = new SpeechPhraseFormatter(new SpeechPhraseFormatterOptions(MinPhraseWords: 6, MaxPhraseWords: 20));

        var segments = new List<SpeakablePhraseSegment>();
        // Split a UTF-16 surrogate pair (rocket emoji U+1F680 = \uD83D\uDE80) across two deltas.
        segments.AddRange(formatter.AppendDelta("## **Summary**\nDr. Smith paid $12.50 for 3.14 kg, e.g. in the U.S. market \uD83D"));
        segments.AddRange(formatter.AppendDelta("\uDE80. Check [the docs](https://example.com/docs) or https://raw.example.com/path. "));
        segments.AddRange(formatter.AppendDelta("```csharp\nvar secret = 42;\n```\nDone now."));
        var tail = formatter.Flush();
        if (tail is not null)
        {
            segments.Add(tail);
        }

        Assert.NotEmpty(segments);
        var joined = string.Join(" ", segments.Select(s => s.SpokenText));
        Assert.Contains("Dr. Smith paid $12.50 for 3.14 kg, e.g. in the U.S. market \U0001F680.", joined);
        Assert.Contains("the docs", joined);
        Assert.DoesNotContain("https://", joined);
        Assert.DoesNotContain("**", joined);
        Assert.DoesNotContain("##", joined);
        Assert.DoesNotContain("var secret = 42", joined);
        Assert.Contains("Done now.", joined);

        // Verify sequential segment indexing and non-empty source mapping.
        for (var i = 0; i < segments.Count; i++)
        {
            Assert.Equal(i, segments[i].SegmentIndex);
            Assert.False(string.IsNullOrWhiteSpace(segments[i].SourceText));
        }
    }

    [Fact]
    public void AdaptiveEndpointEvaluator_WaitsOnHesitationEndpointsOnClearUtteranceAndFiltersBackchannels()
    {
        var normal = new AdaptiveEndpointEvaluator(conservativeMode: false);
        var conservative = new AdaptiveEndpointEvaluator(conservativeMode: true);

        // Hesitation waits past normal silence.
        var hesitation = normal.Evaluate(new AdaptiveEndpointInput(
            SilenceDurationMs: 800,
            PartialTranscript: "Can you show me the... umm...",
            TranscriptRevision: 4,
            StableDurationMs: 400,
            IsAssistantSpeaking: false,
            SpeechDetected: false));
        Assert.Equal(AdaptiveEndpointDecision.ContinueListening, hesitation.Decision);
        Assert.False(hesitation.Authoritative);

        // Clear completed utterance endpoints quickly in normal mode, waits longer in conservative mode.
        var clearInput = new AdaptiveEndpointInput(
            SilenceDurationMs: 500,
            PartialTranscript: "Show my outstanding tasks.",
            TranscriptRevision: 5,
            StableDurationMs: 300,
            IsAssistantSpeaking: false,
            SpeechDetected: false);
        Assert.Equal(AdaptiveEndpointDecision.EndpointReady, normal.Evaluate(clearInput).Decision);
        Assert.Equal(AdaptiveEndpointDecision.ContinueListening, conservative.Evaluate(clearInput).Decision);

        // Backchannel during assistant playback is ignored.
        var backchannel = normal.Evaluate(new AdaptiveEndpointInput(
            SilenceDurationMs: 200,
            PartialTranscript: "mhm",
            TranscriptRevision: 1,
            StableDurationMs: 100,
            IsAssistantSpeaking: true,
            SpeechDetected: false));
        Assert.Equal(AdaptiveEndpointDecision.BackchannelIgnore, backchannel.Decision);

        // Same word when assistant is silent is not filtered as backchannel.
        var notBackchannel = normal.Evaluate(new AdaptiveEndpointInput(
            SilenceDurationMs: 900,
            PartialTranscript: "yeah",
            TranscriptRevision: 1,
            StableDurationMs: 500,
            IsAssistantSpeaking: false,
            SpeechDetected: false));
        Assert.Equal(AdaptiveEndpointDecision.EndpointReady, notBackchannel.Decision);
    }

    [Fact]
    public void RealtimeTranscriptCollector_TracksMonotonicPartialRevisions()
    {
        var collector = new RealtimeTranscriptCollector();

        Assert.True(collector.TryReadPartial(
            """{"type":"transcript","final":false,"transcript":"hello"}""",
            out var p1,
            out var rev1));
        Assert.Equal("hello", p1!.Text);
        Assert.Equal(1, rev1);

        Assert.True(collector.TryReadPartial(
            """{"type":"transcript","final":false,"revision":5,"transcript":"hello world"}""",
            out var p2,
            out var rev2));
        Assert.Equal("hello world", p2!.Text);
        Assert.Equal(5, rev2);

        // Stale explicit revision is rejected.
        Assert.False(collector.TryReadPartial(
            """{"type":"transcript","final":false,"revision":4,"transcript":"stale"}""",
            out _,
            out _));

        collector.ResetPartialRevision();
        Assert.Equal(0, collector.LastPartialRevision);
    }
}
