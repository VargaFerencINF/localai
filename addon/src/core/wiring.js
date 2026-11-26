// Registers the agent's handlers on the messenger events. Shared by src/background.js (real Thunderbird) and the
// tests / e2e simulation (fake messenger), so both exercise exactly the same wiring.

export function wireAgent(messenger, agent) {
  messenger.runtime.onStartup.addListener(() => agent.onStartup());
  messenger.runtime.onInstalled.addListener(() => agent.onStartup());
  messenger.messages.onNewMailReceived.addListener((folder, messages) => agent.onNewMail(folder, messages));
  messenger.alarms.onAlarm.addListener((alarm) => agent.onAlarm(alarm));
  messenger.menus.onClicked.addListener((info) => agent.onMenuClicked(info));
  messenger.runtime.onMessage.addListener((message) => agent.onRuntimeMessage(message));
}
