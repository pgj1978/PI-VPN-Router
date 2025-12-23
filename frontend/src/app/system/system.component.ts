import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ApiService } from '../services/api.service';

@Component({
  selector: 'app-system',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './system.component.html',
  styleUrl: './system.component.css'
})
export class SystemComponent {
  isRebooting = false;
  message = '';
  error = '';

  constructor(private apiService: ApiService) {}

  confirmReboot() {
    if (confirm('Are you sure you want to reboot the system? This will take the router offline for a minute.')) {
      this.reboot();
    }
  }

  reboot() {
    this.isRebooting = true;
    this.message = 'Initiating reboot...';
    this.error = '';

    this.apiService.rebootSystem().subscribe({
      next: (res) => {
        this.message = 'System is rebooting. Please wait for the router to come back online (approx. 60 seconds).';
        // Reload page after delay? Or just show message.
      },
      error: (err) => {
        this.isRebooting = false;
        this.error = 'Failed to reboot: ' + (err.error?.error || err.message);
        this.message = '';
      }
    });
  }
}
