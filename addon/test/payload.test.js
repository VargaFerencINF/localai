import assert from 'node:assert/strict';
import test from 'node:test';
import { buildPayload, canonicalMessageId, collectInlineTextParts, pickBodies } from '../src/core/payload.js';
import { htmlToText } from '../src/core/htmlToText.js';
import { backoffMs } from '../src/core/backoff.js';
import { replaceAiTag, tagForStatus } from '../src/core/tags.js';
import { DEMO_EML, INBOX, readJson, setup } from './helpers.js';

test('payload built from the demo email equals samples/expected/demo_payload.json', async () => {
  const { exe, fake, agent } = await setup();
  const id = fake.addEml(DEMO_EML, INBOX);
  await agent.refreshMatchers();

  await fake.fireNewMail(INBOX, [id]);

  const [submission] = exe.submissions();
  assert.deepEqual(submission.body, readJson('samples', 'expected', 'demo_payload.json'));
});

test('canonical Message-ID always has angle brackets', () => {
  assert.equal(canonicalMessageId('abc@x'), '<abc@x>');
  assert.equal(canonicalMessageId(' <abc@x> '), '<abc@x>');
  assert.equal(canonicalMessageId(''), '');
});

test('plain text part is preferred; HTML is only sent without a plain part', () => {
  assert.deepEqual(pickBodies([{ contentType: 'text/html', content: '<p>h</p>' }, { contentType: 'text/plain; charset=utf-8', content: 't' }]), { text: 't', html: '<p>h</p>' });
  const payload = buildPayload({ header: { headerMessageId: 'a@b', subject: 's', date: new Date(0), author: 'x', recipients: [] }, folder: { accountId: 'a', path: '/p' }, bodyText: 'conv', bodyHtml: '<b>x</b>', attachmentNames: ['a.pdf'], source: 'manual', ruleId: 'r' });
  assert.equal(payload.bodyHtml, '<b>x</b>');
  assert.equal(payload.ruleId, 'r');
  assert.deepEqual(payload.attachmentNames, ['a.pdf']);
});

test('getFull walk skips attachments and finds nested text parts', () => {
  const parts = collectInlineTextParts({
    contentType: 'multipart/mixed',
    parts: [
      { contentType: 'multipart/alternative', parts: [{ contentType: 'text/plain', body: 'szöveg' }, { contentType: 'text/html', body: '<p>x</p>' }] },
      { contentType: 'text/plain', name: 'melleklet.txt', body: 'nem kell' },
    ],
  });
  assert.deepEqual(parts.map((p) => p.content), ['szöveg', '<p>x</p>']);
});

test('HTML-only message: convertToPlainText is used when available, the fallback otherwise', async () => {
  for (const withConvertToPlainText of [true, false]) {
    const { exe, fake, agent } = await setup({ messengerOptions: { withConvertToPlainText } });
    await agent.refreshMatchers();
    const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés', text: null, html: '<p>Kérünk:</p><ul><li>10 db bot</li></ul>' });
    await fake.fireNewMail(INBOX, [id]);
    const body = exe.submissions()[0].body;
    assert.equal(body.bodyHtml, '<p>Kérünk:</p><ul><li>10 db bot</li></ul>');
    assert.equal(body.bodyText, withConvertToPlainText ? 'Kérünk: 10 db bot' : 'Kérünk:\n\n- 10 db bot');
    assert.equal(fake.callsOf('messengerUtilities.convertToPlainText').length, withConvertToPlainText ? 1 : 0);
  }
});

test('without listInlineTextParts the body comes from getFull', async () => {
  const { exe, fake, agent } = await setup({ messengerOptions: { withInlineTextParts: false } });
  await agent.refreshMatchers();
  const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés', text: 'teljes szöveg', attachments: ['rendeles.xlsx'] });
  await fake.fireNewMail(INBOX, [id]);
  assert.equal(exe.submissions()[0].body.bodyText, 'teljes szöveg');
  assert.deepEqual(exe.submissions()[0].body.attachmentNames, ['rendeles.xlsx']);
  assert.equal(fake.callsOf('messages.getFull').length, 1);
});

test('htmlToText keeps structure', () => {
  assert.equal(htmlToText('<div>Első<br>Második</div><script>x()</script><table><tr><td>a</td><td>b</td></tr></table>&lt;ok&gt; &amp; &#337;'),
    'Első\nMásodik\na b\n<ok> & ő');
});

test('backoff grows and is capped at 60 minutes', () => {
  assert.deepEqual([1, 2, 3, 4, 5, 6, 7, 50].map((n) => backoffMs(n) / 60000), [1, 2, 5, 10, 30, 60, 60, 60]);
});

test('status -> tag mapping and tag replacement keep foreign tags', () => {
  assert.deepEqual(['queued', 'processing', 'done', 'needs_review', 'failed'].map(tagForStatus), ['sorban', 'sorban', 'kesz', 'ellenorzendo', 'hiba']);
  assert.deepEqual(replaceAiTag(['$label1', 'efai_sorban'], ['efai_sorban', 'efai_kesz'], 'efai_kesz'), ['$label1', 'efai_kesz']);
});
