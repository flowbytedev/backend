import type { ReactElement } from 'react';
import { NextResponse } from 'next/server';
import { getResend } from '../../../../emails/src/lib/resend';
import PipelineExportEmail from '../../../../emails/pipeline-export';

interface AttachmentPayload {
  filename: string;
  /** Base64. Passed straight to Resend, which accepts a base64 string for `content`. */
  content: string;
}

interface PipelineExportEmailRequest {
  from: string;
  to: string | string[];
  cc?: string | string[];
  bcc?: string | string[];
  replyTo?: string | string[];
  subject: string;
  message?: string;
  pipelineName: string;
  runId?: string;
  rowCount?: number;
  fileName?: string;
  fileSizeLabel?: string;
  linkUrl?: string;
  linkLabel?: string;
  linkReason?: string;
  attachments?: AttachmentPayload[];
}

/**
 * Resend's documented ceiling is 40MB on the assembled message. This route refuses earlier so the caller
 * gets a clear reason instead of a provider error, and so a runaway body is rejected before it is decoded
 * into a Buffer. Base64 is ~4/3 of the file, so 30MB of base64 is a ~22MB attachment.
 */
const MAX_TOTAL_BASE64 = 30 * 1024 * 1024;

const hasRecipients = (value: string | string[] | undefined) =>
  Array.isArray(value) ? value.length > 0 : Boolean(value);

export async function POST(request: Request) {
  let payload: PipelineExportEmailRequest;

  try {
    payload = (await request.json()) as PipelineExportEmailRequest;
  } catch {
    return NextResponse.json(
      { status: 'ERROR', message: 'The request body is not valid JSON.' },
      { status: 400 }
    );
  }

  const errors: string[] = [];

  if (!payload?.from) errors.push('Missing `from` address.');
  if (!hasRecipients(payload?.to)) errors.push('Missing `to` recipient.');
  if (!payload?.subject) errors.push('Missing email subject.');
  if (!payload?.pipelineName) errors.push('Missing `pipelineName`.');

  const attachments = Array.isArray(payload?.attachments) ? payload.attachments : [];

  for (const attachment of attachments) {
    if (!attachment?.filename) errors.push('An attachment has no filename.');
    if (!attachment?.content) errors.push(`Attachment "${attachment?.filename}" has no content.`);
  }

  const totalBase64 = attachments.reduce((sum, a) => sum + (a?.content?.length || 0), 0);

  if (totalBase64 > MAX_TOTAL_BASE64) {
    errors.push(
      `Attachments total ${(totalBase64 / (1024 * 1024)).toFixed(1)}MB encoded, over the ` +
        `${MAX_TOTAL_BASE64 / (1024 * 1024)}MB limit for one message.`
    );
  }

  if (errors.length > 0) {
    return NextResponse.json(
      { status: 'ERROR', message: errors.join(' ') },
      { status: 400 }
    );
  }

  try {
    const { data, error } = await getResend().emails.send({
      from: payload.from,
      to: payload.to,
      ...(hasRecipients(payload.cc) ? { cc: payload.cc } : {}),
      ...(hasRecipients(payload.bcc) ? { bcc: payload.bcc } : {}),
      ...(hasRecipients(payload.replyTo) ? { replyTo: payload.replyTo } : {}),
      subject: payload.subject,
      react: PipelineExportEmail({
        pipelineName: payload.pipelineName,
        runId: payload.runId,
        rowCount: payload.rowCount ?? 0,
        message: payload.message,
        fileName: payload.fileName,
        fileSizeLabel: payload.fileSizeLabel,
        linkUrl: payload.linkUrl,
        linkLabel: payload.linkLabel,
        linkReason: payload.linkReason,
      }) as ReactElement,
      ...(attachments.length > 0
        ? {
            attachments: attachments.map((a) => ({
              filename: a.filename,
              content: a.content,
            })),
          }
        : {}),
    });

    // The SDK reports a rejected send in `error` rather than by throwing, so returning 200 here without
    // checking it would tell the pipeline the mail was delivered when it was not — and a run that goes
    // green having delivered nothing is the failure mode this whole feature has to avoid.
    if (error) {
      console.error('Resend refused the pipeline export email', error);
      return NextResponse.json(
        { status: 'ERROR', message: error.message || 'Resend refused the message.' },
        { status: 502 }
      );
    }

    return NextResponse.json({ status: 'OK', id: data?.id });
  } catch (error: unknown) {
    console.error('Failed to send pipeline export email', error);
    return NextResponse.json(
      {
        status: 'ERROR',
        message: error instanceof Error ? error.message : 'Failed to dispatch the export email.',
      },
      { status: 500 }
    );
  }
}

export async function GET() {
  return NextResponse.json({ status: 'OK' });
}
