import test from 'node:test';
import assert from 'node:assert/strict';
import { mountHRChatbot, SpeechTranscript, validateNavigationTarget } from '../public/widget.js';

const origins = { currentOrigin: 'https://hr.example.com' };

test('navigation accepts static relative paths and the configured HTTPS origin only', () => {
  assert.equal(validateNavigationTarget('/hr/attendance\n', origins), '/hr/attendance');
  assert.equal(validateNavigationTarget('https://hr.example.com/hr/policies', origins), 'https://hr.example.com/hr/policies');
  assert.equal(validateNavigationTarget('https://other.example.com/hr/policies', origins), null);
  assert.equal(validateNavigationTarget('https://other.example.com/hr/policies', { ...origins, allowedNavigationOrigin: 'https://other.example.com' }), 'https://other.example.com/hr/policies');
});

test('navigation rejects scripts, external hosts, credentials, query strings, encoded and ambiguous paths', () => {
  for (const value of [
    'javascript:alert(1)', 'data:text/html,test', '//evil.example.com/hr',
    'https://hr.example.com@evil.example.com/hr', 'https://user@hr.example.com/hr',
    '/hr/attendance?user=1', '/hr/policies#unsafe', '/hr/%2f%2fevil', '/hr/../admin',
    'https://hr.example.com/hr/../admin', 'https://hr.example.com/%2e%2e/admin',
    '/hr\\admin', '/hr//admin', '/hr/a b', '<a href="/hr">click</a>',
    'http://hr.example.com/hr', 'https://hr.example.com:444/hr', 'https://hr.example.com/hr\n/another',
  ]) assert.equal(validateNavigationTarget(value, origins), null, value);
  assert.equal(validateNavigationTarget('/' + 'a'.repeat(2048), origins), null);
});

test('navigation bounds path length and rejects long invalid suffixes without ambiguous regex backtracking', () => {
  assert.equal(validateNavigationTarget('/' + 'a'.repeat(199), origins), '/' + 'a'.repeat(199));
  assert.equal(validateNavigationTarget('/' + 'a'.repeat(200), origins), null);
  assert.equal(validateNavigationTarget('https://hr.example.com/' + 'a'.repeat(200), origins), null);
  for (let index = 0; index < 1000; index += 1) {
    assert.equal(validateNavigationTarget('/' + 'a'.repeat(198) + '!', origins), null);
    assert.equal(validateNavigationTarget('/' + 'a'.repeat(199) + '!', origins), null);
  }
  assert.equal(validateNavigationTarget('/hr/attendance\n/other', origins), null);
  assert.equal(validateNavigationTarget('https://hr.example.com/hr/attendance\n/other', origins), null);
});

test('speech results preserve the draft and do not duplicate cumulative final results', () => {
  const transcript = new SpeechTranscript('Tôi muốn xem');
  const final = (text) => Object.assign([{ transcript: text }], { isFinal: true });
  const interim = (text) => Object.assign([{ transcript: text }], { isFinal: false });
  assert.equal(transcript.update([final('giờ làm'), interim('hôm')]), 'Tôi muốn xem giờ làm hôm');
  assert.equal(transcript.update([final('giờ làm'), final('hôm nay')]), 'Tôi muốn xem giờ làm hôm nay');
  assert.equal(transcript.update([final('giờ làm'), final('hôm nay')]), 'Tôi muốn xem giờ làm hôm nay');
  assert.equal(transcript.value(), 'Tôi muốn xem giờ làm hôm nay');
});

test('unfinalized speech is not sent and transcription has the API input length limit', () => {
  const transcript = new SpeechTranscript('Nội quy');
  transcript.update([Object.assign([{ transcript: 'công ty' }], { isFinal: false })]);
  assert.equal(transcript.value(), 'Nội quy');
  const long = new SpeechTranscript('a'.repeat(1490));
  assert.equal(long.update([Object.assign([{ transcript: 'b'.repeat(100) }], { isFinal: true })]).length, 1500);
});

class FakeElement {
  constructor(tagName, document) {
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = document;
    this.children = [];
    this.attributes = {};
    this.listeners = {};
    this.value = '';
    this.className = '';
    this.textContent = '';
  }
  append(...children) {
    for (const child of children) {
      child.parent = this;
      this.children.push(child);
    }
  }
  replaceChildren(...children) { this.children = []; this.append(...children); }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, listener) { (this.listeners[name] ||= []).push(listener); }
  focus() { this.ownerDocument.activeElement = this; }
  remove() {
    if (this.parent) this.parent.children = this.parent.children.filter((child) => child !== this);
  }
  fire(name, properties = {}) {
    const event = { preventDefault() {}, ...properties };
    return Promise.all((this.listeners[name] || []).map((listener) => listener(event)));
  }
  requestSubmit() { return this.fire('submit'); }
}

function descendants(element) { return [element, ...element.children.flatMap(descendants)]; }
function find(element, className) { return descendants(element).find((node) => node.className.split(' ').includes(className)); }

function fixture({ fetch, speech = false, synchronousStop = false } = {}) {
  const calls = [];
  const browser = {
    location: { href: 'https://hr.example.com/dashboard', origin: 'https://hr.example.com', hostname: 'hr.example.com' },
    isSecureContext: true,
    setTimeout,
    clearTimeout,
    fetch: async (...args) => {
      calls.push(args);
      return fetch ? fetch(...args) : new Response('/hr/attendance', { headers: { 'content-type': 'text/plain' } });
    },
  };
  if (speech) {
    browser.SpeechRecognition = class {
      constructor() { browser.recognition = this; }
      start() {}
      stop() {
        if (synchronousStop) this.onend?.();
        else queueMicrotask(() => this.onend?.());
      }
      abort() { this.onend?.(); }
    };
  }
  const document = {
    defaultView: browser,
    createElement(tag) { return new FakeElement(tag, document); },
  };
  const container = document.createElement('div');
  const dispose = mountHRChatbot(container, { endpoint: '/demo/chat', suggestions: ['Xem nội quy công ty'] });
  return { browser, document, container, dispose, calls };
}

test('widget sends only edited text and displays an assistant message containing only a link', async () => {
  const view = fixture();
  const input = find(view.container, 'hr-chatbot__input');
  input.value = 'Hôm nay tôi đã làm bao nhiêu giờ?';
  await input.fire('input');
  await find(view.container, 'hr-chatbot__composer').fire('submit');
  assert.equal(view.calls.length, 1);
  assert.deepEqual(JSON.parse(view.calls[0][1].body), { message: 'Hôm nay tôi đã làm bao nhiêu giờ?' });
  assert.equal(view.calls[0][1].credentials, 'same-origin');
  assert.equal(view.calls[0][1].headers.get('Accept'), 'text/plain');
  const reply = find(view.container, 'hr-chatbot__message--assistant');
  assert.equal(reply.children.length, 1);
  assert.equal(reply.children[0].tagName, 'A');
  assert.equal(reply.children[0].textContent, '/hr/attendance');
  assert.equal(reply.children[0].href, '/hr/attendance');
  assert.equal(input.value, '');
  view.dispose();
});

test('widget keeps invalid links out of the transcript and preserves the draft on failure', async () => {
  const view = fixture({ fetch: async () => new Response('javascript:alert(1)', { headers: { 'content-type': 'text/plain' } }) });
  const input = find(view.container, 'hr-chatbot__input');
  input.value = 'Nội quy';
  await find(view.container, 'hr-chatbot__composer').fire('submit');
  assert.equal(find(view.container, 'hr-chatbot__message--assistant'), undefined);
  assert.match(find(view.container, 'hr-chatbot__status').textContent, /chưa được cho phép/u);
  assert.equal(input.value, 'Nội quy');
  view.dispose();
});

test('speech never sends automatically; user stops, edits, then submits', async () => {
  const view = fixture({ speech: true });
  const input = find(view.container, 'hr-chatbot__input');
  const microphone = find(view.container, 'hr-chatbot__microphone');
  input.value = 'Xem';
  await microphone.fire('click');
  assert.equal(view.browser.recognition.lang, 'vi-VN');
  assert.equal(input.readOnly, true);
  view.browser.recognition.onresult({ results: [Object.assign([{ transcript: 'nội quy' }], { isFinal: true })] });
  assert.equal(input.value, 'Xem nội quy');
  await find(view.container, 'hr-chatbot__composer').fire('submit');
  assert.equal(view.calls.length, 0);
  await microphone.fire('click');
  await Promise.resolve();
  assert.equal(input.readOnly, false);
  input.value = 'Xem nội quy công ty';
  await find(view.container, 'hr-chatbot__composer').fire('submit');
  assert.deepEqual(JSON.parse(view.calls[0][1].body), { message: 'Xem nội quy công ty' });
  view.dispose();
});

test('widget rejects an off-origin chat endpoint before sending user text', () => {
  const view = fixture();
  assert.throws(() => mountHRChatbot(view.container, { endpoint: 'https://evil.example.com/chat' }), /same origin/u);
  view.dispose();
});

test('disposing aborts an in-flight request and removes the widget', async () => {
  let signal;
  const view = fixture({ fetch: async (_url, options) => {
    signal = options.signal;
    return new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError'))));
  } });
  find(view.container, 'hr-chatbot__input').value = 'Nội quy công ty';
  const completion = find(view.container, 'hr-chatbot__composer').fire('submit');
  view.dispose();
  await completion;
  assert.equal(signal.aborted, true);
  assert.equal(view.container.children.length, 0);
});

test('microphone denial unlocks editing even if the browser does not fire onend', async () => {
  const view = fixture({ speech: true });
  const input = find(view.container, 'hr-chatbot__input');
  input.value = 'Bản nháp';
  await find(view.container, 'hr-chatbot__microphone').fire('click');
  view.browser.recognition.onerror({ error: 'not-allowed' });
  assert.equal(input.value, 'Bản nháp');
  assert.equal(input.readOnly, false);
  assert.equal(find(view.container, 'hr-chatbot__send').disabled, false);
  assert.match(find(view.container, 'hr-chatbot__status').textContent, /quyền micro/u);
  assert.equal(view.calls.length, 0);
  view.dispose();
});

test('synchronous recognition stop and late events cannot overwrite subsequent edits', async () => {
  const view = fixture({ speech: true, synchronousStop: true });
  const input = find(view.container, 'hr-chatbot__input');
  const microphone = find(view.container, 'hr-chatbot__microphone');
  await microphone.fire('click');
  const oldRecognition = view.browser.recognition;
  oldRecognition.onresult({ results: [Object.assign([{ transcript: 'Nội quy' }], { isFinal: true })] });
  await microphone.fire('click');
  input.value = 'Nội quy công ty đã sửa';
  oldRecognition.onresult({ results: [Object.assign([{ transcript: 'Kết quả đến muộn' }], { isFinal: true })] });
  oldRecognition.onend();
  assert.equal(input.value, 'Nội quy công ty đã sửa');
  assert.equal(input.readOnly, false);
  view.dispose();
});

test('suggestions populate an editable draft without sending a request', async () => {
  const view = fixture();
  await find(view.container, 'hr-chatbot__suggestion').fire('click');
  assert.equal(find(view.container, 'hr-chatbot__input').value, 'Xem nội quy công ty');
  assert.equal(view.calls.length, 0);
  assert.equal(find(view.container, 'hr-chatbot__send').disabled, false);
  view.dispose();
});

test('transport errors remain UI status and never become assistant answers', async () => {
  for (const response of [
    new Response('unauthorized', { status: 401 }),
    new Response('<html>login</html>', { headers: { 'content-type': 'text/html' } }),
  ]) {
    const view = fixture({ fetch: async () => response });
    find(view.container, 'hr-chatbot__input').value = 'Xem nội quy';
    await find(view.container, 'hr-chatbot__composer').fire('submit');
    assert.equal(find(view.container, 'hr-chatbot__message--assistant'), undefined);
    assert.match(find(view.container, 'hr-chatbot__status').className, /--error/u);
    view.dispose();
  }
});

test('disposing an active microphone disconnects callbacks and aborts capture', async () => {
  const view = fixture({ speech: true });
  await find(view.container, 'hr-chatbot__microphone').fire('click');
  let aborted = false;
  view.browser.recognition.abort = () => { aborted = true; };
  view.dispose();
  assert.equal(aborted, true);
  assert.equal(view.browser.recognition.onresult, null);
  assert.equal(view.browser.recognition.onend, null);
  assert.equal(view.container.children.length, 0);
});
