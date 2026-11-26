import assert from 'node:assert/strict';
import test from 'node:test';
import { findBestMatch, normalizeSubject, normalizeText } from '../src/core/normalize.js';
import { readJson } from './helpers.js';

// The same vectors as src/EfMailAgent.Tests/Matching/NormalizationCasesTests.cs: C# and JS must agree 100%.
const cases = readJson('shared', 'normalization-cases.json');

test('the shared file has at least 25 cases', () => {
  assert.ok(cases.normalize.length + cases.matching.reduce((n, s) => n + s.cases.length, 0) >= 25);
});

for (const [index, c] of cases.normalize.entries()) {
  test(`normalize #${index}: ${c.note}`, () => {
    assert.equal(normalizeSubject(c.input), c.expected);
    assert.equal(normalizeSubject(c.expected), c.expected, 'idempotent');
  });
}

for (const suite of cases.matching) {
  for (const c of suite.cases) {
    test(`match [${suite.name}] "${c.subject}" -> ${c.expectedRuleId}`, () => {
      assert.equal(findBestMatch(c.subject, suite.rules)?.ruleId ?? null, c.expectedRuleId);
    });
  }
}

test('keywords keep a leading prefix-like word', () => {
  assert.equal(normalizeText('Re: fontos'), 're: fontos');
});

test('an invalid regex never matches and does not throw', () => {
  assert.equal(findBestMatch('rma-1', [{ ruleId: 'x', priority: 1, subjectContains: [], subjectRegex: '(' }]), null);
});
