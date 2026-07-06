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

interface CommentMentionEmailProps {
  recipientName?: string;
  mentionedByName: string;
  datasetName: string;
  tableName: string;
  commentContent: string;
  commentUrl?: string;
}

const email_signature = process.env.EMAIL_SIGNATURE || 'The Data Team';

export const CommentMentionEmail: React.FC<Readonly<CommentMentionEmailProps>> = ({
  recipientName,
  mentionedByName,
  datasetName,
  tableName,
  commentContent,
  commentUrl,
}) => {
  return (
    <Html>
      <Head />
      <Preview>{`${mentionedByName} mentioned you in a comment`}</Preview>
      <Body style={main}>
        <Container style={container}>
          <Section style={{ marginBottom: '8px' }}>
            <span style={badge}>MENTION</span>
          </Section>

          <Heading style={heading}>You were mentioned in a comment</Heading>

          <Text style={paragraph}>
            {recipientName ? `Hi ${recipientName}, ` : ''}
            {`${mentionedByName} mentioned you in a comment on `}
            <strong>{datasetName}</strong>
            {` / `}
            <strong>{tableName}</strong>.
          </Text>

          <Section style={card}>
            <Text style={cardLabel}>Comment</Text>
            <Text style={commentText}>{commentContent}</Text>
          </Section>

          {commentUrl ? (
            <Section style={{ textAlign: 'center', margin: '28px 0' }}>
              <Button style={button} href={commentUrl}>
                View comment
              </Button>
            </Section>
          ) : null}

          <Text style={muted}>
            You are receiving this because you were mentioned in a comment.
          </Text>
          <Text style={signature}>{email_signature}</Text>
        </Container>
      </Body>
    </Html>
  );
};

export default CommentMentionEmail;

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

const commentText: React.CSSProperties = {
  fontSize: '15px',
  lineHeight: '23px',
  color: '#111827',
  whiteSpace: 'pre-wrap',
  margin: 0,
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
