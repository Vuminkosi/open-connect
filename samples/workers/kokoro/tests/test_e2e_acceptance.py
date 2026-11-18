#!/usr/bin/env python3
"""Offline Kokoro contract checks, not evidence of model-live synthesis.

Exercise real encoding/validation code. Model output, host epoch disposal, and
speech quality require the separately approved model-live/integration tiers.
"""
from __future__ import annotations

import io
import json
from contextlib import nullcontext
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
import wave

import numpy as np
from pydantic import ValidationError

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / 'workers' / 'kokoro' / 'src'))

from openconnect_kokoro.config import Settings  # noqa: E402
from openconnect_kokoro.engine import (  # noqa: E402
    KokoroEngine, ModelUnavailable, VoiceUnknown, _pcm16le, _wav,
)
from openconnect_kokoro.schemas import SynthesizePayload  # noqa: E402

FIXTURES = SAMPLE_DIR / 'fixtures'


class KokoroWorkerAcceptanceTests(unittest.TestCase):
    def setUp(self) -> None:
        # Any unexpected attempt to provision a model fails the test immediately.
        guard = patch('openconnect_kokoro.engine._ensure_artifacts',
                      side_effect=AssertionError('contract tier must not provision models'))
        guard.start()
        self.addCleanup(guard.stop)
        self.engine = KokoroEngine('/unused-contract-models')

    def fixture(self, name: str) -> dict:
        return json.loads((FIXTURES / f'{name}.json').read_text(encoding='utf-8'))

    def synthesize(self, **changes):
        args = dict(text='Hello', voice_id='af_heart', locale='en',
                    audio_format='wav', idempotency_key=None)
        args.update(changes)
        return self.engine.synthesize(**args)

    def test_at_045_audio_encoding_contract_not_model_synthesis(self) -> None:
        """Actual WAV/PCM codecs produce valid 24kHz mono 16-bit audio containers."""
        expected = self.fixture('short-text-af-heart')['expectedBehavior']
        samples = np.array([-2, -1, 0, 1, 2], dtype=np.float32)
        pcm = _pcm16le(samples)
        self.assertEqual(np.frombuffer(pcm, dtype='<i2').tolist(),
                         [-32767, -32767, 0, 32767, 32767])
        with wave.open(io.BytesIO(_wav(samples)), 'rb') as audio:
            self.assertEqual(audio.getframerate(), expected['expectedSampleRateHz'])
            self.assertEqual(audio.getnchannels(), expected['expectedChannels'])
            self.assertEqual(audio.getsampwidth(), 2)
            self.assertEqual(audio.getnframes(), len(samples))
            self.assertEqual(audio.readframes(len(samples)), pcm)

    def test_at_046_excessive_text_bounds(self) -> None:
        limit = self.fixture('excessive-text')['maxChars']
        with patch.object(self.engine, 'ready', side_effect=ModelUnavailable('not provisioned')) as ready:
            with self.assertRaisesRegex(ValueError, '1-2000 characters'):
                self.synthesize(text='a' * (limit + 1))
            ready.assert_not_called()
            with self.assertRaises(ModelUnavailable):
                self.synthesize(text='a' * limit)
            ready.assert_called_once()

    def test_at_047_invalid_voice_rejection(self) -> None:
        with patch.object(self.engine, 'ready') as ready:
            with self.assertRaises(VoiceUnknown):
                self.synthesize(voice_id=self.fixture('invalid-voice')['input']['voiceId'])
            ready.assert_not_called()

    def test_at_048_empty_text_rejection(self) -> None:
        with patch.object(self.engine, 'ready') as ready:
            for text in ('', self.fixture('empty-text')['input']['text']):
                with self.subTest(text=text), self.assertRaisesRegex(ValueError, '1-2000'):
                    self.synthesize(text=text)
            ready.assert_not_called()

    def test_at_049_epoch_schema_passthrough_not_stream_disposal(self) -> None:
        """Worker accepts epoch metadata; downstream stale-chunk disposal is not tested here."""
        epoch = self.fixture('interrupted-stream-epoch')['presentedEpoch']
        body = SynthesizePayload(schemaVersion='openconnect.v1', text='Hello', speechEpoch=epoch)
        self.assertEqual(body.speechEpoch, epoch)
        with self.assertRaises(ValidationError):
            SynthesizePayload(schemaVersion='openconnect.v1', text='Hello', speechEpoch=-1)

    def test_at_050_markup_passed_literally_and_empty_output_fails_closed(self) -> None:
        text = self.fixture('malformed-markup')['input']['text']
        pipeline = Mock(return_value=[])
        self.engine._pipeline = pipeline
        self.engine._voice_file = Path('/unused-contract-models/voices/af_heart.pt')
        # No torch/model import or fabricated successful speech. Only an empty
        # pipeline double is used to observe exact arguments and fail-closed output.
        with patch.dict(sys.modules, {'torch': SimpleNamespace(inference_mode=nullcontext)}):
            with self.assertRaisesRegex(ModelUnavailable, 'produced no audio'):
                self.synthesize(text=text)
        pipeline.assert_called_once_with(text, voice=str(self.engine._voice_file), speed=0.95)

    def test_at_051_voice_inventory_cannot_expand_fixed_engine(self) -> None:
        fx = self.fixture('changed-voice-manifest')
        for voices, accepted in ((fx['initialVoices'], True), (fx['updatedVoices'], False)):
            with self.subTest(voices=voices), patch.dict('os.environ', {'KOKORO_VOICES': ','.join(voices)}):
                if accepted:
                    self.assertEqual(Settings.from_environment().voices, ('af_heart',))
                else:
                    with self.assertRaisesRegex(ValueError, 'only af_heart'):
                        Settings.from_environment()
        with self.assertRaises(VoiceUnknown):
            self.synthesize(voice_id='af_bella')

    def test_mp3_rejected_before_model_loading(self) -> None:
        with patch.object(self.engine, 'ready') as ready:
            with self.assertRaisesRegex(ValueError, 'mp3 output is not supported'):
                self.synthesize(audio_format='mp3')
            ready.assert_not_called()


if __name__ == '__main__':
    unittest.main(verbosity=2)
