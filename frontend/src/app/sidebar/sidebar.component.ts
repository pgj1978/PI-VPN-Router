import { Component, Output, EventEmitter, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.css'
})
export class SidebarComponent {
  menuOpen = true; // Desktop state
  @Input() mobileOpen = false; // Mobile state
  @Output() toggle = new EventEmitter<boolean>();
  @Output() closeMobile = new EventEmitter<void>();

  toggleMenu() {
    this.menuOpen = !this.menuOpen;
    this.toggle.emit(this.menuOpen);
  }

  onMobileLinkClick() {
    this.closeMobile.emit();
  }
}