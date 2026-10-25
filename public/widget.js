const SAFE_PATH = /^\/[A-Za-z0-9_/-]*$/;
const MAX_PATH_LENGTH = 200;
const MAX_MESSAGE_LENGTH = 1500;
let nextWidgetId = 0;

function isSafePath(value) {
  return value.length <= MAX_PATH_LENGTH && !value.includes('//') && SAFE_PATH.test(value);
}

/** Validate a link before placing it in the DOM. Never trust a model response. */
export function validateNavigationTarget(value, { currentOrigin, allowedNavigationOrigin = currentOrigin }) {
  if (typeof value !== 'string' || value.length > 2048) return null;
  const target = value.trim();
  if (!target || /[\s\\%?#]/u.test(target)) return null;
  if (isSafePath(target)) return target;
  const match = /^https?:\/\/[^/]+(\/.*)?$/u.exec(target);
  if (!match || !isSafePath(match[1] || '/')) return null;
  try {
    const link = new URL(target);
    const allowed = new URL(allowedNavigationOrigin);
    const current = new URL(currentOrigin);
    if (allowed.username || allowed.password || allowed.search || allowed.hash || allowed.pathname !== '/') return null;
    if (allowed.protocol !== 'https:' && allowed.origin !== current.origin) return null;
    if (link.username || link.password || link.origin !== allowed.origin) return null;
    if (link.protocol !== 'https:' && link.origin !== current.origin) return null;
    return link.href;
  } catch {
    return null;
  }
}

/** Speech results are cumulative; rebuild them rather than appending duplicates. */
export class SpeechTranscript {
  constructor(baseText = '') {
    this.baseText = baseText;
    this.finalText = '';
    this.interimText = '';
  }

  update(results) {
    const finalParts = [];
    const interimParts = [];
    for (let index = 0; index < results.length; index += 1) {
      const result = results[index];
      const text = result[0]?.transcript?.trim();
      if (text) (result.isFinal ? finalParts : interimParts).push(text);
    }
    this.finalText = finalParts.join(' ');
    this.interimText = interimParts.join(' ');
    return this.value(true);
  }

  value(includeInterim = false) {
    const speech = [this.finalText, includeInterim ? this.interimText : ''].filter(Boolean).join(' ');
    return [this.baseText, speech].filter(Boolean).join(this.baseText.endsWith(' ') ? '' : ' ').slice(0, MAX_MESSAGE_LENGTH);
  }
}

/**
 * Mount a framework-independent widget in a Razor page or any HTML application.
 * endpoint must be your authenticated, same-origin HRM backend endpoint.
 * Service credentials belong exclusively in that backend.
 */
export function mountHRChatbot(container, options = {}) {
  if (!container?.ownerDocument) throw new TypeError('A DOM container is required.');
  const document = container.ownerDocument;
  const browser = document.defaultView;
  const endpoint = new URL(options.endpoint || '/api/hr-chat', browser.location.href);
  if (endpoint.origin !== browser.location.origin || !['http:', 'https:'].includes(endpoint.protocol) || endpoint.username || endpoint.password || endpoint.hash) {
    throw new TypeError('The chatbot endpoint must be on the same origin as the HRM page.');
  }
  const id = `hr-chatbot-${++nextWidgetId}`;
  let disposed = false;
  let pending = false;
  let listening = false;
  let stopping = false;
  let recognition = null;
  let transcript = null;
  let requestController = null;
  let requestTimer = null;
  let stopTimer = null;
  let speechError = false;
  const SpeechRecognition = browser.SpeechRecognition || browser.webkitSpeechRecognition;

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  const root = element('section', 'hr-chatbot');
  root.setAttribute('aria-label', options.title || 'Trợ lý điều hướng nhân sự');
  const header = element('header', 'hr-chatbot__header');
  const avatar = element('span', 'hr-chatbot__avatar', 'A');
  avatar.setAttribute('aria-hidden', 'true');
  const headingGroup = element('div');
  const heading = element('h2', 'hr-chatbot__title', options.title || 'Trợ lý nhân sự');
  const subtitle = element('p', 'hr-chatbot__subtitle', 'Hỏi bằng giọng nói hoặc văn bản');
  headingGroup.append(heading, subtitle);
  header.append(avatar, headingGroup);

  const body = element('div', 'hr-chatbot__body');
  const intro = element('div', 'hr-chatbot__intro');
  intro.append(element('p', 'hr-chatbot__intro-title', 'Bạn muốn xem thông tin gì?'));
  intro.append(element('p', '', 'Tôi sẽ gửi đường dẫn đến màn hình phù hợp. Bạn có thể sửa câu đã nói trước khi gửi.'));
  body.append(intro);

  const suggestions = element('div', 'hr-chatbot__suggestions');
  suggestions.setAttribute('aria-label', 'Câu hỏi gợi ý');
  const suggestionButtons = [];
  for (const prompt of options.suggestions || []) {
    if (typeof prompt !== 'string' || prompt.length > MAX_MESSAGE_LENGTH) continue;
    const button = element('button', 'hr-chatbot__suggestion', prompt);
    button.type = 'button';
    button.addEventListener('click', () => {
      if (pending || listening || stopping || disposed) return;
      input.value = prompt;
      input.focus();
      refresh();
    });
    suggestionButtons.push(button);
    suggestions.append(button);
  }
  body.append(suggestions);

  const messages = element('ol', 'hr-chatbot__messages');
  messages.setAttribute('role', 'log');
  messages.setAttribute('aria-live', 'polite');
  messages.setAttribute('aria-label', 'Lịch sử hội thoại');
  body.append(messages);

  const form = element('form', 'hr-chatbot__composer');
  const label = element('label', 'hr-chatbot__label', 'Nội dung yêu cầu');
  label.htmlFor = `${id}-message`;
  const input = element('textarea', 'hr-chatbot__input');
  input.id = `${id}-message`;
  input.name = 'message';
  input.rows = 3;
  input.maxLength = MAX_MESSAGE_LENGTH;
  input.placeholder = 'Ví dụ: Hôm nay tôi đã làm bao nhiêu giờ?';
  input.setAttribute('aria-describedby', `${id}-status ${id}-privacy`);

  const toolbar = element('div', 'hr-chatbot__toolbar');
  const microphone = element('button', 'hr-chatbot__microphone', '● Bắt đầu nói');
  microphone.type = 'button';
  microphone.setAttribute('aria-pressed', 'false');
  const characterCount = element('span', 'hr-chatbot__count', `0/${MAX_MESSAGE_LENGTH}`);
  characterCount.setAttribute('aria-hidden', 'true');
  const send = element('button', 'hr-chatbot__send', 'Gửi yêu cầu');
  send.type = 'submit';
  toolbar.append(microphone, characterCount, send);
  const status = element('p', 'hr-chatbot__status');
  status.id = `${id}-status`;
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  const privacy = element('p', 'hr-chatbot__privacy', 'Nhận giọng nói do trình duyệt cung cấp và có thể sử dụng dịch vụ của nhà cung cấp trình duyệt. Không đọc nội dung nhạy cảm vào micro.');
  privacy.id = `${id}-privacy`;
  form.append(label, input, toolbar, status, privacy);
  root.append(header, body, form);
  container.replaceChildren(root);

  const secureContext = browser.isSecureContext || ['localhost', '127.0.0.1', '[::1]'].includes(browser.location.hostname);
  const canListen = Boolean(SpeechRecognition && secureContext);
  if (!secureContext) {
    setStatus('Micro cần HTTPS hoặc localhost. Bạn vẫn có thể nhập văn bản.');
  } else if (!SpeechRecognition) {
    setStatus('Trình duyệt này chưa hỗ trợ nhận giọng nói. Hãy nhập văn bản hoặc dùng Chrome/Edge có hỗ trợ Web Speech.');
  } else {
    setStatus('Bấm “Bắt đầu nói”, dừng lại, kiểm tra bản chép rồi bấm “Gửi yêu cầu”.');
  }
  refresh();

  function setStatus(message, error = false) {
    if (disposed) return;
    status.textContent = message;
    status.className = error ? 'hr-chatbot__status hr-chatbot__status--error' : 'hr-chatbot__status';
  }

  function refresh() {
    if (disposed) return;
    const speechBusy = listening || stopping;
    input.readOnly = pending || speechBusy;
    send.disabled = pending || speechBusy || !input.value.trim();
    send.textContent = pending ? 'Đang tìm đường dẫn…' : 'Gửi yêu cầu';
    microphone.disabled = !canListen || pending || stopping;
    microphone.textContent = stopping ? 'Đang dừng…' : listening ? '■ Dừng nghe' : '● Bắt đầu nói';
    microphone.setAttribute('aria-pressed', String(speechBusy));
    characterCount.textContent = `${input.value.length}/${MAX_MESSAGE_LENGTH}`;
    for (const button of suggestionButtons) button.disabled = pending || speechBusy;
  }

  function finishSpeech(active = recognition) {
    if (disposed || !active || recognition !== active) return;
    if (stopTimer) browser.clearTimeout(stopTimer);
    stopTimer = null;
    if (transcript) input.value = transcript.value(false);
    recognition = null;
    listening = false;
    stopping = false;
    if (!speechError) setStatus('Đã dừng nghe. Bạn có thể sửa bản chép và bấm “Gửi yêu cầu”.');
    refresh();
    input.focus();
  }

  function stopSpeech() {
    if (!recognition || stopping) return;
    stopping = true;
    refresh();
    const active = recognition;
    try {
      stopTimer = browser.setTimeout(() => {
        try { active.abort(); } catch { /* Recognition may have already ended. */ }
        finishSpeech(active);
      }, 2000);
      active.stop();
    } catch {
      finishSpeech(active);
    }
  }

  microphone.addEventListener('click', () => {
    if (!canListen || pending || stopping || disposed) return;
    if (listening) {
      stopSpeech();
      return;
    }
    speechError = false;
    transcript = new SpeechTranscript(input.value);
    recognition = new SpeechRecognition();
    const active = recognition;
    active.lang = options.speechLanguage || 'vi-VN';
    active.continuous = true;
    active.interimResults = true;
    active.maxAlternatives = 1;
    active.onresult = (event) => {
      if (disposed || recognition !== active) return;
      input.value = transcript.update(event.results);
      refresh();
      if (input.value.length >= MAX_MESSAGE_LENGTH) stopSpeech();
    };
    active.onerror = (event) => {
      if (disposed || recognition !== active) return;
      speechError = true;
      const errors = {
        'not-allowed': 'Chưa có quyền micro. Hãy cho phép micro trong trình duyệt hoặc nhập văn bản.',
        'service-not-allowed': 'Trình duyệt không cho phép dịch vụ nhận giọng nói. Bạn có thể nhập văn bản.',
        'audio-capture': 'Không tìm thấy micro khả dụng. Hãy kiểm tra thiết bị hoặc nhập văn bản.',
        'no-speech': 'Chưa nhận được giọng nói. Bạn có thể thử lại hoặc nhập văn bản.',
        network: 'Dịch vụ nhận giọng nói không kết nối được. Bạn vẫn có thể nhập văn bản.',
        'language-not-supported': 'Dịch vụ chưa hỗ trợ tiếng Việt trên thiết bị này. Bạn có thể nhập văn bản.',
        aborted: 'Đã dừng nhận giọng nói. Bạn có thể sửa nội dung trước khi gửi.',
      };
      setStatus(errors[event.error] || 'Không nhận được giọng nói. Bạn có thể thử lại hoặc nhập văn bản.', true);
      finishSpeech(active);
      try { active.abort(); } catch { /* Some implementations have already stopped. */ }
    };
    active.onend = () => {
      if (recognition === active) finishSpeech(active);
    };
    listening = true;
    refresh();
    setStatus('Đang nghe tiếng Việt… Bấm “Dừng nghe” khi nói xong. Bản chép chưa được gửi.');
    try {
      active.start();
    } catch {
      speechError = true;
      setStatus('Không thể bật micro. Hãy kiểm tra quyền truy cập hoặc nhập văn bản.', true);
      finishSpeech(active);
    }
  });

  input.addEventListener('input', refresh);
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) {
      event.preventDefault();
      if (!send.disabled) form.requestSubmit();
    }
  });

  function appendMessage(kind, text) {
    const item = element('li', `hr-chatbot__message hr-chatbot__message--${kind}`);
    if (kind === 'assistant') {
      const link = element('a', 'hr-chatbot__link', text);
      link.href = text;
      item.append(link);
    } else {
      item.append(element('p', '', text));
    }
    messages.append(item);
    body.scrollTop = body.scrollHeight;
  }

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const message = input.value.trim();
    if (disposed || pending || listening || stopping || !message || message.length > MAX_MESSAGE_LENGTH) return;
    pending = true;
    refresh();
    appendMessage('user', message);
    setStatus('Đang tìm màn hình phù hợp…');
    requestController = new AbortController();
    let timedOut = false;
    requestTimer = browser.setTimeout(() => {
      timedOut = true;
      requestController?.abort();
    }, 20000);
    try {
      const headers = new Headers(options.fetchOptions?.headers);
      headers.set('Content-Type', 'application/json');
      headers.set('Accept', 'text/plain');
      const response = await browser.fetch(endpoint.href, {
        ...options.fetchOptions,
        method: 'POST',
        credentials: options.fetchOptions?.credentials || 'same-origin',
        headers,
        redirect: 'error',
        body: JSON.stringify({ message }),
        signal: requestController.signal,
      });
      if (!response.ok) {
        const errorMessages = {
          401: 'Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại vào hệ thống nhân sự.',
          403: 'Yêu cầu chưa được hệ thống cho phép. Hãy tải lại trang và thử lại.',
          429: 'Bạn đang gửi quá nhanh. Hãy đợi một lát rồi thử lại.',
        };
        throw new Error(errorMessages[response.status] || 'Chưa kết nối được trợ lý. Hãy thử lại sau.');
      }
      if (!(response.headers.get('content-type') || '').toLowerCase().startsWith('text/plain')) {
        throw new Error('Phản hồi không đúng định dạng đường dẫn. Hãy liên hệ quản trị hệ thống.');
      }
      const url = validateNavigationTarget(await response.text(), {
        currentOrigin: browser.location.origin,
        allowedNavigationOrigin: options.allowedNavigationOrigin || browser.location.origin,
      });
      if (!url) throw new Error('Đường dẫn phản hồi chưa được cho phép. Hãy kiểm tra cấu hình tích hợp.');
      if (disposed) return;
      appendMessage('assistant', url);
      input.value = '';
      setStatus('Đã nhận đường dẫn. Bấm đường dẫn để mở màn hình tương ứng.');
    } catch (error) {
      if (disposed) return;
      const message = timedOut ? 'Yêu cầu mất quá nhiều thời gian. Hãy thử lại.' : error?.name === 'TypeError' ? 'Không kết nối được trợ lý. Hãy kiểm tra kết nối và thử lại.' : error.message || 'Có lỗi kết nối. Hãy thử lại.';
      setStatus(message, true);
    } finally {
      if (requestTimer) browser.clearTimeout(requestTimer);
      requestTimer = null;
      requestController = null;
      pending = false;
      refresh();
      if (!disposed) input.focus();
    }
  });

  return function dispose() {
    if (disposed) return;
    disposed = true;
    requestController?.abort();
    if (requestTimer) browser.clearTimeout(requestTimer);
    if (stopTimer) browser.clearTimeout(stopTimer);
    if (recognition) {
      recognition.onend = null;
      recognition.onerror = null;
      recognition.onresult = null;
      try { recognition.abort(); } catch { /* Already inactive. */ }
    }
    root.remove();
  };
}
