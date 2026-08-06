import { Component, inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

export interface LogDialogData {
  title: string;
  log: string;
  error: string | null;
}

/**
 * Shows raw command output from a failed operation.
 *
 * When wg-quick refuses to bring a tunnel up, its stderr is the only thing that says why.
 * Reducing that to "Connection failed" is what made the last outage take as long as it did.
 */
@Component({
  selector: 'app-log-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      @if (data.error) {
        <div class="error">
          <mat-icon>error_outline</mat-icon>
          <span>{{ data.error }}</span>
        </div>
      }
      @if (data.log) {
        <pre class="log mono">{{ data.log }}</pre>
      } @else {
        <p class="muted">No command output was captured.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button (click)="copy()">
        <mat-icon>content_copy</mat-icon>
        Copy
      </button>
      <button mat-flat-button (click)="ref.close()">Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .error {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      padding: 12px;
      margin-bottom: 12px;
      border-radius: 8px;
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .log {
      background: var(--mat-sys-surface-container-highest);
      padding: 12px;
      border-radius: 8px;
      max-height: 45vh;
      overflow: auto;
      white-space: pre-wrap;
      word-break: break-word;
      margin: 0;
    }
  `,
})
export class LogDialog {
  protected readonly ref = inject(MatDialogRef<LogDialog>);
  protected readonly data = inject<LogDialogData>(MAT_DIALOG_DATA);

  protected copy(): void {
    const text = [this.data.error, this.data.log].filter(Boolean).join('\n\n');
    void navigator.clipboard?.writeText(text);
  }
}
