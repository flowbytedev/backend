import { Resend } from 'resend';

let client: Resend | undefined;

/**
 * Resolved on first send rather than at module load. `next build` imports every
 * route handler to collect page data, and the build agent has no
 * RESEND_API_KEY -- constructing the client eagerly fails the build.
 */
export function getResend(): Resend {
  if (!client) {
    const apiKey = process.env.RESEND_API_KEY;
    if (!apiKey) {
      throw new Error('RESEND_API_KEY is not set');
    }

    client = new Resend(apiKey);
  }

  return client;
}
