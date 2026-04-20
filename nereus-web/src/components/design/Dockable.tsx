import type { DragEvent as ReactDragEvent, ReactNode } from 'react';

type DockableProps = {
  title: ReactNode;
  children: ReactNode;
  ledOn?: boolean;
  ledTx?: boolean;
  actions?: ReactNode;
  className?: string;
  draggable?: boolean;
  onDragStart?: (e: ReactDragEvent<HTMLDivElement>) => void;
  onDragOver?: (e: ReactDragEvent<HTMLDivElement>) => void;
  onDrop?: (e: ReactDragEvent<HTMLDivElement>) => void;
};

export function Dockable({
  title,
  children,
  ledOn,
  ledTx,
  actions,
  className = '',
  draggable,
  onDragStart,
  onDragOver,
  onDrop,
}: DockableProps) {
  return (
    <div
      className={`panel ${className}`}
      draggable={draggable}
      onDragStart={onDragStart}
      onDragOver={onDragOver}
      onDrop={onDrop}
    >
      <div className="panel-head">
        <span className={`dot ${ledTx ? 'tx' : ledOn ? 'on' : ''}`} />
        <span className="title">{title}</span>
        <span className="spacer" style={{ flex: 1 }} />
        {actions}
        <button className="btn ghost sm" title="Drag to rearrange" style={{ cursor: 'grab' }}>
          ⋮⋮
        </button>
      </div>
      <div className="panel-body">{children}</div>
    </div>
  );
}
