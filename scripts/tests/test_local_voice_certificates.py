import pathlib
import queue
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "setup-cloud-local-voice-certificates.ps1"


@unittest.skipUnless(sys.platform == "win32" and shutil.which("openssl"), "Windows and OpenSSL required")
class LocalVoiceCertificatesTests(unittest.TestCase):
    def test_identities_are_isolated_and_gateway_certificate_authenticates(self):
        with tempfile.TemporaryDirectory(prefix="oc-voice-cert-test-") as temporary:
            target = pathlib.Path(temporary) / "certificates"
            command = ["powershell.exe", "-NoProfile", "-NonInteractive", "-File", str(SCRIPT), "-OutputDirectory", str(target)]
            result = subprocess.run(command, capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = {"authority": {"authority.key"}, "gateway": {"gateway.key"},
                        "voice": {"voice.key", "worker-client.key"}, "screen": {"screen.key"}}
            for role, names in expected.items():
                self.assertEqual({p.name for p in (target / role).glob("*.key")}, names)
            self.assertFalse((target / "issuer").exists())
            wrong_identity = subprocess.run(
                ["openssl", "verify", "-purpose", "sslclient", "-CAfile", str(target / "voice/gateway.crt"),
                 str(target / "voice/worker-client.crt")], capture_output=True, text=True, timeout=10)
            self.assertNotEqual(wrong_identity.returncode, 0)

            server_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            server_context.load_cert_chain(target / "voice/voice.crt", target / "voice/voice.key")
            server_context.load_verify_locations(target / "voice/gateway.crt")
            server_context.verify_mode = ssl.CERT_REQUIRED
            client_context = ssl.create_default_context(cafile=target / "gateway/voice.crt")
            client_context.load_cert_chain(target / "gateway/gateway.crt", target / "gateway/gateway.key")
            events = queue.Queue()
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                listener.listen(1)
                listener.settimeout(10)

                def serve():
                    try:
                        connection, _ = listener.accept()
                        with connection, server_context.wrap_socket(connection, server_side=True) as secured:
                            secured.sendall(b"ready")
                        events.put(None)
                    except Exception as error:
                        events.put(error)

                thread = threading.Thread(target=serve, daemon=True)
                thread.start()
                with socket.create_connection(listener.getsockname(), timeout=10) as connection:
                    with client_context.wrap_socket(connection, server_hostname="localhost") as secured:
                        self.assertEqual(secured.recv(5), b"ready")
                self.assertIsNone(events.get(timeout=10))
                thread.join(timeout=10)

            before = (target / "gateway/gateway.key").read_bytes()
            retry = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertNotEqual(retry.returncode, 0)
            self.assertEqual((target / "gateway/gateway.key").read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
