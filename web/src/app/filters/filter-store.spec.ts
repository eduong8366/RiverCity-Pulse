import { Location } from '@angular/common';
import { provideLocationMocks, SpyLocation } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { FilterStore } from './filter-store';
import { DEFAULT_FILTERS } from './filters';

describe('FilterStore', () => {
  function start(url: string): { store: FilterStore; location: SpyLocation } {
    TestBed.configureTestingModule({ providers: [provideLocationMocks()] });
    const location = TestBed.inject(Location) as SpyLocation;
    location.replaceState(url);
    return { store: TestBed.inject(FilterStore), location };
  }

  it('starts from the URL', () => {
    const { store } = start('/?window=30&district=2');

    expect(store.filters()).toEqual({ ...DEFAULT_FILTERS, window: 30, district: 2 });
  });

  it('writes changes back to the URL without the defaults', () => {
    const { store, location } = start('/?window=30');

    store.window.set(90);
    store.category.set('Parking');
    TestBed.tick();

    expect(location.urlChanges.at(-1)).toBe('replace: /?category=Parking');
  });
});
