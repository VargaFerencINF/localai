// MV3 event page: thin wiring of the real Thunderbird API into the core. Listeners are registered synchronously at
// the top level so that Thunderbird can wake the page for them.
import { createAgent } from './core/agent.js';
import { wireAgent } from './core/wiring.js';

wireAgent(messenger, createAgent({ messenger, fetch: (...args) => fetch(...args), log: console }));
