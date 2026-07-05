import type { Env } from './types';
import { corsHeaders as adminCorsHeaders } from './admin-api';

export function adminRateLimitedResponse(env: Env, request: Request): Response {
  return new Response('rate limited', { status: 429, headers: adminCorsHeaders(env, request) });
}
