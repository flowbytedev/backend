import type { ReactElement } from 'react';
import { NextResponse } from 'next/server';
import { getResend } from '../../../../emails/src/lib/resend';
import IncidentNotificationEmail from '../../../../emails/incident-notification';

interface IncidentEmailRequest {
  from: string;
  to: string | string[];
  subject: string;
  entityName: string;
  incidentTitle: string;
  severity?: string;
  message?: string;
  statusUrl: string;
}

export async function POST(request: Request) {
  const payload = (await request.json()) as IncidentEmailRequest;

  const errors: string[] = [];

  if (!payload?.from) {
    errors.push('Missing `from` address.');
  }

  const hasRecipients = Array.isArray(payload?.to)
    ? payload.to.length > 0
    : Boolean(payload?.to);

  if (!hasRecipients) {
    errors.push('Missing `to` recipient.');
  }

  if (!payload?.subject) {
    errors.push('Missing email subject.');
  }

  if (!payload?.entityName) {
    errors.push('Missing `entityName`.');
  }

  if (!payload?.statusUrl) {
    errors.push('Missing `statusUrl`.');
  }

  if (errors.length > 0) {
    return NextResponse.json(
      {
        status: 'ERROR',
        message: errors.join(' '),
      },
      { status: 400 }
    );
  }

  try {
    await getResend().emails.send({
      from: payload.from,
      to: payload.to,
      subject: payload.subject,
      react: IncidentNotificationEmail({
        entityName: payload.entityName,
        incidentTitle: payload.incidentTitle,
        severity: payload.severity,
        message: payload.message,
        statusUrl: payload.statusUrl,
      }) as ReactElement,
    });

    return NextResponse.json({
      status: 'OK',
    });
  } catch (error: unknown) {
    console.error('Failed to send incident notification email', error);
    return NextResponse.json(
      {
        status: 'ERROR',
        message: 'Failed to dispatch incident notification email.',
      },
      { status: 500 }
    );
  }
}

export async function GET() {
  return NextResponse.json({
    status: 'OK',
  });
}
