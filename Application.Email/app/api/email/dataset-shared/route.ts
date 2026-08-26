import type { ReactElement } from 'react';
import { NextResponse } from 'next/server';
import { Resend } from 'resend';
import DatasetSharedEmail from '../../../../emails/dataset-shared';

const resend = new Resend(process.env.RESEND_API_KEY);

interface DatasetSharedEmailRequest {
  from: string;
  to: string | string[];
  subject: string;
  recipientName?: string;
  datasetName: string;
  sharedByName: string;
  accessLevel: string;
  datasetUrl?: string;
  message?: string;
}

export async function POST(request: Request) {
  const payload = (await request.json()) as DatasetSharedEmailRequest;

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

  if (!payload?.datasetName) {
    errors.push('Missing `datasetName`.');
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
    await resend.emails.send({
      from: payload.from,
      to: payload.to,
      subject: payload.subject,
      react: DatasetSharedEmail({
        recipientName: payload.recipientName,
        datasetName: payload.datasetName,
        sharedByName: payload.sharedByName,
        accessLevel: payload.accessLevel,
        datasetUrl: payload.datasetUrl,
        message: payload.message,
      }) as ReactElement,
    });

    return NextResponse.json({
      status: 'OK',
    });
  } catch (error: unknown) {
    console.error('Failed to send dataset shared email', error);
    return NextResponse.json(
      {
        status: 'ERROR',
        message: 'Failed to dispatch dataset shared email.',
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
