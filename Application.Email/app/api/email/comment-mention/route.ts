import { NextResponse } from 'next/server';
import { Resend } from 'resend';
import CommentMentionEmail from '../../../../emails/comment-mention';

const resend = new Resend(process.env.RESEND_API_KEY);

interface CommentMentionEmailRequest {
  from: string;
  to: string | string[];
  subject: string;
  recipientName?: string;
  mentionedByName: string;
  datasetName: string;
  tableName: string;
  commentContent: string;
  commentUrl?: string;
}

export async function POST(request: Request) {
  const payload = (await request.json()) as CommentMentionEmailRequest;

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
    await resend.emails.send({
      from: payload.from,
      to: payload.to,
      subject: payload.subject,
      react: CommentMentionEmail({
        recipientName: payload.recipientName,
        mentionedByName: payload.mentionedByName,
        datasetName: payload.datasetName,
        tableName: payload.tableName,
        commentContent: payload.commentContent,
        commentUrl: payload.commentUrl,
      }),
    });

    return NextResponse.json({
      status: 'OK',
    });
  } catch (error: unknown) {
    console.error('Failed to send comment mention email', error);
    return NextResponse.json(
      {
        status: 'ERROR',
        message: 'Failed to dispatch comment mention email.',
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
