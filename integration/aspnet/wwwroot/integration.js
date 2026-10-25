import { mountHRChatbot } from './chatbot/widget.js';

const container = document.querySelector('#ahr-chatbot');
const verificationToken = document.querySelector('#chat-antiforgery input[name="__RequestVerificationToken"]')?.value;
if (container && verificationToken) {
  const dispose = mountHRChatbot(container, {
    endpoint: container.dataset.endpoint,
    fetchOptions: { headers: { RequestVerificationToken: verificationToken } },
    speechLanguage: 'vi-VN',
  });
  window.addEventListener('pagehide', dispose, { once: true });
}
