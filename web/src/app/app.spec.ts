import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  it('shows the product name and the source note', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideLocationMocks()],
    });
    const fixture = TestBed.createComponent(App);
    // Not whenStable: the API requests stay pending (nothing answers them here).
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('h1')?.textContent).toBe('RiverCity Pulse');
    expect(page.querySelector('footer')?.textContent).toContain(
      'not affiliated with or endorsed by the City of Sacramento',
    );
  });
});
