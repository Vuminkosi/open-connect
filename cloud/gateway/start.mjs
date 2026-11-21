import { createLocalServer } from './local-server.mjs';
import { authorizationFromEnvironment, classificationProbeFromEnvironment, classificationExecutionFromEnvironment } from './configuration.mjs';
import { inferenceTokenizerFromEnvironment } from './inference-tokenizer-configuration.mjs';
import { inferenceExecutionFromEnvironment } from './inference-execution-configuration.mjs';
import { liveUIExecutionFromEnvironment } from './liveui-execution-configuration.mjs';
import { voiceSynthesisFromEnvironment } from './voice-synthesis-configuration.mjs';

const authorization = authorizationFromEnvironment(process.env);
const classificationProbe = classificationProbeFromEnvironment(process.env);
const classificationExecution = classificationExecutionFromEnvironment(process.env, authorization, classificationProbe);
const inferenceTokenizer = inferenceTokenizerFromEnvironment(process.env);
const inferenceExecution = inferenceExecutionFromEnvironment(process.env, authorization, inferenceTokenizer);
const liveUIExecution = liveUIExecutionFromEnvironment(process.env, authorization);
const voiceExecution = voiceSynthesisFromEnvironment(process.env, authorization);
const server = createLocalServer(authorization, classificationProbe, classificationExecution, inferenceExecution, liveUIExecution, voiceExecution);
server.on('close', () => { classificationExecution?.keyring.dispose(); inferenceExecution?.keyring.dispose(); inferenceTokenizer?.dispose(); liveUIExecution?.keyring.dispose(); voiceExecution?.keyring.dispose(); });
server.listen(Number(process.env.PORT || 8091), '0.0.0.0');
