import type { ReactElement } from 'react';
import { NextResponse } from 'next/server';
import { getResend } from '../../../../emails/src/lib/resend';
import TableMovedEmail from '../../../../emails/table-moved';

interface TableMovedEmailRequest {
  from: string;
  to: string | string[];
  subject: string;
  recipientName?: string;
  movedByName: string;
  tableName: string;
  oldDatasetName: string;
  newDatasetName: string;
  newDatasetUrl?: string;
}

export async function POST(request: Request) {
  const payload = (await request.json()) as TableMovedEmailRequest;

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
      react: TableMovedEmail({
        recipientName: payload.recipientName,
        movedByName: payload.movedByName,
        tableName: payload.tableName,
        oldDatasetName: payload.oldDatasetName,
        newDatasetName: payload.newDatasetName,
        newDatasetUrl: payload.newDatasetUrl,
      }) as ReactElement,
    });

    return NextResponse.json({
      status: 'OK',
    });
  } catch (error: unknown) {
    console.error('Failed to send table moved email', error);
    return NextResponse.json(
      {
        status: 'ERROR',
        message: 'Failed to dispatch table moved email.',
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
