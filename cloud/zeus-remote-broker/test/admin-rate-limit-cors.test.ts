/**
 * Regression coverage for the top-level /admin rate-limit response.
 * It must use the admin CORS allowlist, not the public /turn WEB_APP_ORIGIN
 * helper, otherwise /admin/login errors are unreadable from the apex dashboard.
 *
 *   node --import tsx --test test/admin-rate-limit-cors.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { adminRateLimitedResponse } from '../src/admin-rate-limit-response.ts';
import type { Env } from '../src/types.ts';

function rateLimitedEnv(): Env {
  return {
    WEB_APP_ORIGIN: 'https://app.openhpsdrzeus.com',
    ADMIN_ORIGINS: 'https://openhpsdrzeus.com',
  } as unknown as Env;
}

test('rate-limited admin responses echo the dashboard origin, not WEB_APP_ORIGIN', async () => {
  const res = adminRateLimitedResponse(
    rateLimitedEnv(),
    new Request('https://remote.openhpsdrzeus.com/admin/login', {
      method: 'POST',
      headers: {
        Origin: 'https://openhpsdrzeus.com',
        'content-type': 'application/json',
      },
      body: '{}',
    }),
  );

  assert.equal(res.status, 429);
  assert.equal(res.headers.get('Access-Control-Allow-Origin'), 'https://openhpsdrzeus.com');
  assert.notEqual(res.headers.get('Access-Control-Allow-Origin'), 'https://app.openhpsdrzeus.com');
});
