import type { ReactElement } from 'react';
import { NextResponse } from 'next/server';
import { Resend } from 'resend';
import SalesSnapshotEmail from '../../../../../emails/sales-snapshot';

const resend = new Resend(process.env.RESEND_API_KEY);

type TrendDirection = 'up' | 'down' | 'flat';

interface DataTableRow {
  Data?: { [key: string]: any };
  [key: string]: any;
}

interface MetricCard {
  label: string;
  value: string;
  changeLabel?: string;
  changeDirection?: TrendDirection;
  helperText?: string;
}

interface SchemeKpiCard {
  schemeName: string;
  totalSales: string;
  deltaLabel?: string;
  changeDirection?: TrendDirection;
  stores?: number;
  transactions?: number;
}

interface SalesSnapshotRequest {
  from: string;
  to: string | string[];
  recipientName?: string;
  title: string;
  description?: string;
  lastUpdatedLabel?: string;
  totalKpis?: MetricCard[];
  schemeKpis?: SchemeKpiCard[];
  Columns?: string[];
  Rows?: DataTableRow[];
}

export async function POST(request: Request) {
  const payload = (await request.json()) as SalesSnapshotRequest;

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

  if (!payload?.title) {
    errors.push('Missing email subject/title.');
  }

  if (!payload?.totalKpis || payload.totalKpis.length === 0) {
    errors.push('At least one total KPI card is required.');
  }

  if (!payload?.Columns || payload.Columns.length === 0) {
    errors.push('At least one table column is required.');
  }

  if (!payload?.Rows || payload.Rows.length === 0) {
    errors.push('At least one table row is required.');
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
      subject: payload.title,
      react: SalesSnapshotEmail({
        recipientName: payload.recipientName,
        title: payload.title,
        description: payload.description,
        lastUpdatedLabel: payload.lastUpdatedLabel,
        totalKpis: payload.totalKpis ?? [],
        schemeKpis: payload.schemeKpis ?? [],
        Columns: payload.Columns ?? [],
        Rows: payload.Rows ?? [],
      }) as ReactElement,
    });

    return NextResponse.json({
      status: 'OK',
    });
  } catch (error: unknown) {
    console.error('Failed to send sales snapshot email', error);
    return NextResponse.json(
      {
        status: 'ERROR',
        message: 'Failed to dispatch sales snapshot email.',
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
