// SPDX-License-Identifier: GPL-3.0-or-later
import { useState, type ButtonHTMLAttributes, type ReactNode } from 'react';
import {
  CalendarClock,
  Check,
  CircleCheck,
  CircleDashed,
  CirclePause,
  CircleX,
  Copy,
  Hourglass,
  LoaderCircle,
  TriangleAlert,
} from 'lucide-react';
import type { BrandStatus } from './statusMapping';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
  compact?: boolean;
}

export function Button({
  variant = 'secondary',
  compact = false,
  className = '',
  type = 'button',
  ...rest
}: ButtonProps) {
  const classes = ['rc-btn', `rc-btn-${variant}`, compact ? 'rc-btn-compact' : '', className]
    .filter(Boolean)
    .join(' ');
  return <button type={type} className={classes} {...rest} />;
}

export function StatusBadge({ kind, label }: { kind: BrandStatus; label: string }) {
  const iconProps = { size: 14, strokeWidth: 1.75, 'aria-hidden': true as const };
  switch (kind) {
    case 'succeeded':
      return (
        <span className="rc-badge rc-badge-success">
          <CircleCheck {...iconProps} />
          {label}
        </span>
      );
    case 'failed':
      return (
        <span className="rc-badge rc-badge-danger">
          <CircleX {...iconProps} />
          {label}
        </span>
      );
    case 'needs-attention':
      return (
        <span className="rc-badge rc-badge-warning">
          <TriangleAlert {...iconProps} />
          {label}
        </span>
      );
    case 'running':
      return (
        <span className="rc-badge rc-badge-primary">
          <LoaderCircle {...iconProps} className="rc-spin" />
          {label}
        </span>
      );
    case 'scheduled':
      return (
        <span className="rc-badge rc-badge-info">
          <CalendarClock {...iconProps} />
          {label}
        </span>
      );
    case 'awaiting-approval':
      return (
        <span className="rc-badge rc-badge-info">
          <Hourglass {...iconProps} />
          {label}
        </span>
      );
    case 'paused':
      return (
        <span className="rc-badge rc-badge-muted">
          <CirclePause {...iconProps} />
          {label}
        </span>
      );
    case 'queued':
    default:
      return (
        <span className="rc-badge rc-badge-muted">
          <CircleDashed {...iconProps} />
          {label}
        </span>
      );
  }
}

export function FormField({
  id,
  label,
  optional = false,
  helper,
  error,
  children,
}: {
  id: string;
  label: string;
  optional?: boolean;
  helper?: string;
  error?: string;
  children: ReactNode;
}) {
  return (
    <div className="rc-field">
      <label className="rc-label" htmlFor={id}>
        {label}
        {optional ? <span className="rc-muted"> (optional)</span> : null}
      </label>
      {children}
      {helper && !error ? (
        <span className="rc-field-helper" id={`${id}-helper`}>
          {helper}
        </span>
      ) : null}
      {error ? (
        <span className="rc-field-error" id={`${id}-error`} role="alert">
          <CircleX size={14} strokeWidth={1.75} aria-hidden="true" />
          {error}
        </span>
      ) : null}
    </div>
  );
}

export function AlertBanner({
  tone,
  title,
  children,
  action,
}: {
  tone: 'info' | 'warning' | 'danger' | 'success';
  title?: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className={`rc-banner rc-banner-${tone}`} role={tone === 'danger' ? 'alert' : 'status'}>
      <div className="rc-stack">
        {title ? <strong>{title}</strong> : null}
        <div>{children}</div>
      </div>
      {action ? <div>{action}</div> : null}
    </div>
  );
}

export function EmptyState({
  icon,
  message,
  action,
}: {
  icon: ReactNode;
  message: string;
  action?: ReactNode;
}) {
  return (
    <div className="rc-empty">
      <div aria-hidden="true">{icon}</div>
      <p style={{ margin: 0 }}>{message}</p>
      {action}
    </div>
  );
}

export function CredentialDisplay({
  label,
  value,
  note,
}: {
  label: string;
  value: string;
  note?: string;
}) {
  const [copied, setCopied] = useState(false);

  const handleCopy = () => {
    void navigator.clipboard.writeText(value.replace(/\s+/g, '')).then(() => {
      setCopied(true);
      window.setTimeout(() => {
        setCopied(false);
      }, 2000);
    });
  };

  return (
    <div className="rc-credential-box">
      <div className="rc-stack">
        <span className="rc-eyebrow">{label}</span>
        <span className="rc-credential-code">{value}</span>
        {note ? <span className="rc-muted">{note}</span> : null}
      </div>
      <Button variant="secondary" compact onClick={handleCopy}>
        {copied ? <Check size={16} aria-hidden="true" /> : <Copy size={16} aria-hidden="true" />}
        {copied ? 'Copied' : 'Copy'}
      </Button>
    </div>
  );
}
