import {
  Body,
  Button,
  Container,
  Head,
  Heading,
  Html,
  Preview,
  Section,
  Text,
} from '@react-email/components';

interface TableMovedEmailProps {
  recipientName?: string;
  movedByName: string;
  tableName: string;
  oldDatasetName: string;
  newDatasetName: string;
  newDatasetUrl?: string;
}

const email_signature = process.env.EMAIL_SIGNATURE || 'The Data Team';

export const TableMovedEmail: React.FC<Readonly<TableMovedEmailProps>> = ({
  recipientName,
  movedByName,
  tableName,
  oldDatasetName,
  newDatasetName,
  newDatasetUrl,
}) => {
  return (
    <Html>
      <Head />
      <Preview>{`"${tableName}" was moved to ${newDatasetName}`}</Preview>
      <Body style={main}>
        <Container style={container}>
          <Section style={{ marginBottom: '8px' }}>
            <span style={badge}>TABLE MOVED</span>
          </Section>

          <Heading style={heading}>A table you have access to was moved</Heading>

          <Text style={paragraph}>
            {recipientName ? `Hi ${recipientName}, ` : ''}
            {`${movedByName} moved the table `}
            <strong>{tableName}</strong>
            {` from `}
            <strong>{oldDatasetName}</strong>
            {` to `}
            <strong>{newDatasetName}</strong>
            {`. Your access to this table has moved with it.`}
          </Text>

          {newDatasetUrl ? (
            <Section style={{ textAlign: 'center', margin: '28px 0' }}>
              <Button style={button} href={newDatasetUrl}>
                View table in {newDatasetName}
              </Button>
            </Section>
          ) : null}

          <Text style={muted}>
            You are receiving this because you had access to this table.
          </Text>
          <Text style={signature}>{email_signature}</Text>
        </Container>
      </Body>
    </Html>
  );
};

export default TableMovedEmail;

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
  color: '#ffffff',
  backgroundColor: '#4f46e5',
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
  margin: '0 0 20px',
};

const button: React.CSSProperties = {
  color: '#ffffff',
  backgroundColor: '#4f46e5',
  fontSize: '14px',
  fontWeight: 600,
  borderRadius: '8px',
  padding: '12px 22px',
  textDecoration: 'none',
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
