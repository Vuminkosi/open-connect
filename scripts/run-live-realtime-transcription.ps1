$ErrorActionPreference = 'Stop'
$python = @'
import asyncio,json,websockets
async def main():
    finals=[]
    async with websockets.connect('ws://127.0.0.1:8080/internal/v1/realtime', additional_headers={'Authorization':'Bearer e2e-worker-token-do-not-use-in-prod'}, open_timeout=15) as ws:
        ack=json.loads(await asyncio.wait_for(ws.recv(),15))
        if not ack.get('sessionId'): raise RuntimeError('missing realtime session acknowledgement')
        pcm=b'\x00'*48000
        for offset in range(0,len(pcm),7680): await ws.send(pcm[offset:offset+7680])
        await ws.send(json.dumps({'type':'endStream'}))
        try:
            while True:
                message=json.loads(await asyncio.wait_for(ws.recv(),30))
                if message.get('type') == 'transcript' and message.get('final') is True:
                    finals.append(message.get('transcript','').strip())
                if message.get('type') == 'speechComplete':
                    finals.append(message.get('transcript','').strip())
                if finals: break
        except Exception:
            pass
    if not any(finals): raise RuntimeError('no final transcript event received')
    print('PASS realtime transcription session=' + ack['sessionId'] + ' final=' + finals[0])
asyncio.run(main())
'@
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($python))
$output = docker compose -f docker-compose-all.yml exec -T worker-transcription python -c "import base64;exec(base64.b64decode('$encoded'))" 2>&1
if ($LASTEXITCODE -ne 0) { $output; exit $LASTEXITCODE }
$output
if ($output -notmatch 'PASS realtime transcription') { throw 'Realtime transcription assertion failed.' }
