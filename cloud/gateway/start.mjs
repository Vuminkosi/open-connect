import { createLocalServer } from './local-server.mjs';
import { authorizationFromEnvironment } from './configuration.mjs';

createLocalServer(authorizationFromEnvironment(process.env)).listen(Number(process.env.PORT || 8091), '0.0.0.0');
