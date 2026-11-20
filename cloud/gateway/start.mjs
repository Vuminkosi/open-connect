import { createLocalServer } from './local-server.mjs';

createLocalServer().listen(Number(process.env.PORT || 8091), '0.0.0.0');
