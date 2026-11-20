import { createLocalServer } from './local-server.mjs';
import { authorizationFromEnvironment, classificationProbeFromEnvironment, classificationExecutionFromEnvironment } from './configuration.mjs';

const authorization = authorizationFromEnvironment(process.env);
const classificationProbe = classificationProbeFromEnvironment(process.env);
const classificationExecution = classificationExecutionFromEnvironment(process.env, authorization, classificationProbe);
const server = createLocalServer(authorization, classificationProbe, classificationExecution);
server.on('close', () => classificationExecution?.keyring.dispose());
server.listen(Number(process.env.PORT || 8091), '0.0.0.0');
