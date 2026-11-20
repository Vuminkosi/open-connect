import { createLocalServer } from './local-server.mjs';
import { authorizationFromEnvironment, classificationProbeFromEnvironment, classificationExecutionFromEnvironment } from './configuration.mjs';
import { inferenceTokenizerFromEnvironment } from './inference-tokenizer-configuration.mjs';
import { inferenceExecutionFromEnvironment } from './inference-execution-configuration.mjs';

const authorization = authorizationFromEnvironment(process.env);
const classificationProbe = classificationProbeFromEnvironment(process.env);
const classificationExecution = classificationExecutionFromEnvironment(process.env, authorization, classificationProbe);
const inferenceTokenizer = inferenceTokenizerFromEnvironment(process.env);
const inferenceExecution = inferenceExecutionFromEnvironment(process.env, authorization, inferenceTokenizer);
const server = createLocalServer(authorization, classificationProbe, classificationExecution, inferenceExecution);
server.on('close', () => { classificationExecution?.keyring.dispose(); inferenceExecution?.keyring.dispose(); inferenceTokenizer?.dispose(); });
server.listen(Number(process.env.PORT || 8091), '0.0.0.0');
