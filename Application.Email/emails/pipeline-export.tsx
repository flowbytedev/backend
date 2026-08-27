import {
  Body,
  Button,
  Container,
  Head,
  Heading,
  Hr,
  Html,
  Preview,
  Section,
  Text,
} from '@react-email/components';

interface PipelineExportEmailProps {
  pipelineName: string;
  runId?: string;
  rowCount: number;
  /** Free text from the pipeline step. Rendered as paragraphs — never as HTML. */
  message?: string;
  /** Present when a file is attached. */
  fileName?: string;
  fileSizeLabel?: string;
  /** Present instead of a file when the export was too large to attach. */
  linkUrl?: string;
  linkLabel?: string;
  linkReason?: string;
}

const email_signature = process.env.EMAIL_SIGNATURE || 'The Data Team';

/**
 * Read at module scope, like EMAIL_SIGNATURE above. Safe because this template only ever renders on the
 * server (the API route calls it), so a non-`NEXT_PUBLIC_` variable is in scope.
 */
const company_name = process.env.COMPANY_NAME || 'FlowByte';

const accent = '#4f46e5';

/**
 * Formats a row count with thousands separators. Explicitly en-US rather than the server's locale, so the
 * same export does not read as "1,204" on one host and "1.204" on another.
 */
const formatRows = (rows: number) =>
  Number.isFinite(rows) ? new Intl.NumberFormat('en-US').format(rows) : '0';

export const PipelineExportEmail: React.FC<Readonly<PipelineExportEmailProps>> = ({
  pipelineName,
  runId,
  rowCount,
  message,
  fileName,
  fileSizeLabel,
  linkUrl,
  linkLabel,
  linkReason,
}) => {
  const rows = formatRows(rowCount);
  const empty = !rowCount;

  // The message arrives as plain text and is split on blank lines rather than interpolated as markup.
  // It comes from a pipeline config field, so treating it as HTML would make that field an injection point
  // into every recipient's inbox.
  const paragraphs = (message || '')
    .split(/\n{2,}/)
    .map((block) => block.trim())
    .filter(Boolean);

  return (
    <Html>
      <Head />
      <Preview>
        {empty
          ? `${pipelineName} — no rows this run`
          : `${pipelineName} — ${rows} rows`}
      </Preview>
      <Body style={main}>
        <Container style={container}>
          <Section style={{ marginBottom: '8px' }}>
            <span style={badge}>DATA EXPORT</span>
          </Section>

          <Heading style={heading}>{pipelineName}</Heading>

          {paragraphs.length > 0 ? (
            paragraphs.map((block, index) => (
              <Text key={index} style={paragraph}>
                {block}
              </Text>
            ))
          ) : (
            <Text style={paragraph}>
              {empty
                ? 'This export ran and produced no rows.'
                : `This export contains ${rows} rows.`}
            </Text>
          )}

          <Section style={card}>
            <Text style={cardLabel}>Rows</Text>
            <Text style={cardValue}>{rows}</Text>

            {fileName ? (
              <>
                <Text style={{ ...cardLabel, marginTop: '14px' }}>Attached</Text>
                <Text style={cardValue}>
                  {fileName}
                  {fileSizeLabel ? <span style={sizeNote}> · {fileSizeLabel}</span> : null}
                </Text>
              </>
            ) : null}
          </Section>

          {/* The oversize path: no attachment, a link into the app instead. */}
          {linkUrl ? (
            <>
              {linkReason ? <Text style={notice}>{linkReason}</Text> : null}
              <Section style={{ textAlign: 'center', margin: '24px 0' }}>
                <Button style={button} href={linkUrl}>
                  {linkLabel ? `Open ${linkLabel}` : `Open in ${company_name}`}
                </Button>
              </Section>
            </>
          ) : null}

          {!fileName && !linkUrl && !empty ? (
            <Text style={notice}>
              No file was attached to this message. If you expected one, check the pipeline run.
            </Text>
          ) : null}

          <Hr style={rule} />

          <Text style={muted}>
            Sent automatically by a {company_name} data pipeline.
            {runId ? ` Run ${runId}.` : ''}
          </Text>
          <Text style={signature}>{email_signature}</Text>
        </Container>
      </Body>
    </Html>
  );
};

export default PipelineExportEmail;

const main: React.CSSProperties = {
  backgroundColor: '#f3f4f6',
  fontFamily:
    '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
  padding: '24px 0',
};

const container: React.CSSProperties = {
  backgroundColor: '#ffffff',
  borderRadius: '12px',
  padding: '32px',
  maxWidth: '560px',
  margin: '0 auto',
  border: '1px solid #e5e7eb',
};

const badge: React.CSSProperties = {
  display: 'inline-block',
  backgroundColor: accent,
  color: '#ffffff',
  fontSize: '11px',
  fontWeight: 700,
  letterSpacing: '0.05em',
  padding: '4px 10px',
  borderRadius: '9999px',
};

const heading: React.CSSProperties = {
  fontSize: '22px',
  fontWeight: 700,
  color: '#111827',
  margin: '8px 0 12px',
};

const paragraph: React.CSSProperties = {
  fontSize: '15px',
  lineHeight: '24px',
  color: '#374151',
  margin: '0 0 16px',
  whiteSpace: 'pre-line',
};

const card: React.CSSProperties = {
  backgroundColor: '#f9fafb',
  border: '1px solid #e5e7eb',
  borderRadius: '8px',
  padding: '16px',
};

const cardLabel: React.CSSProperties = {
  fontSize: '11px',
  textTransform: 'uppercase',
  letterSpacing: '0.05em',
  color: '#6b7280',
  margin: '0 0 4px',
};

const cardValue: React.CSSProperties = {
  fontSize: '16px',
  fontWeight: 600,
  color: '#111827',
  margin: 0,
  wordBreak: 'break-all',
};

const sizeNote: React.CSSProperties = {
  fontSize: '13px',
  fontWeight: 400,
  color: '#6b7280',
};

const notice: React.CSSProperties = {
  fontSize: '13px',
  lineHeight: '20px',
  color: '#92400e',
  backgroundColor: '#fffbeb',
  border: '1px solid #fde68a',
  borderRadius: '8px',
  padding: '12px 14px',
  margin: '16px 0 0',
};

const button: React.CSSProperties = {
  backgroundColor: accent,
  color: '#ffffff',
  fontSize: '14px',
  fontWeight: 600,
  borderRadius: '8px',
  padding: '12px 22px',
  textDecoration: 'none',
};

const rule: React.CSSProperties = {
  borderColor: '#e5e7eb',
  margin: '28px 0 16px',
};

const muted: React.CSSProperties = {
  fontSize: '12px',
  lineHeight: '18px',
  color: '#9ca3af',
  margin: '0 0 4px',
};

const signature: React.CSSProperties = {
  fontSize: '13px',
  color: '#6b7280',
  margin: '12px 0 0',
};
