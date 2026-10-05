import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { Feature, FeatureCollection, Geometry } from 'geojson';
import * as L from 'leaflet';
import { MapNeighborhood, NeighborhoodBoundaries } from '../api/models';
import { fillFor, legendLabels, NO_DATA, quantileCuts, RAMP, shaded } from './bins';

type Boundary = Feature<Geometry, { NAME: string; slug: string }>;

/**
 * Leaflet choropleth of median days to close per neighborhood: five quantile bins over the neighborhoods with 30+
 * closed requests, the rest grey. Hovering (or focusing) a neighborhood emits its slug.
 */
@Component({
  selector: 'app-neighborhood-map',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './neighborhood-map.html',
  styleUrl: './neighborhood-map.scss',
})
export class NeighborhoodMapComponent {
  readonly boundaries = input<NeighborhoodBoundaries>();
  readonly neighborhoods = input<readonly MapNeighborhood[]>([]);
  readonly hovered = output<string | null>();

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('map');
  private readonly ready = signal(false);
  private readonly highlighted = signal<string | null>(null);
  private map?: L.Map;
  private layer?: L.GeoJSON;

  private readonly bySlug = computed(
    () => new Map(this.neighborhoods().map((n) => [n.slug, n] as const)),
  );
  protected readonly cuts = computed(() =>
    quantileCuts(shaded(this.neighborhoods()).map((n) => n.medianDays as number)),
  );
  protected readonly legend = computed(() =>
    legendLabels(this.cuts()).map((label, i) => ({ label, color: RAMP[i] })),
  );
  protected readonly noData = NO_DATA;

  constructor() {
    afterNextRender(() => {
      // No basemap: the 129 shapes carry the page, and there's no tile service to depend on (or key to hold).
      this.map = L.map(this.host().nativeElement, {
        scrollWheelZoom: false,
        zoomSnap: 0.25,
      });
      this.map.attributionControl.setPrefix(
        'Boundaries: City of Sacramento · <a href="https://leafletjs.com">Leaflet</a>',
      );
      this.ready.set(true);
    });

    // Draw the boundaries once they and the map exist.
    effect(() => {
      const geo = this.boundaries();
      if (!this.ready() || !geo || this.layer || !this.map) {
        return;
      }
      this.layer = L.geoJSON(geo as FeatureCollection, {
        style: (f) => this.style(f as Boundary),
        onEachFeature: (f, layer) => this.wire(f as Boundary, layer as L.Path),
      }).addTo(this.map);
      this.map.fitBounds(this.layer.getBounds(), { padding: [8, 8] });
    });

    // Restyle when the figures or the highlight change.
    effect(() => {
      this.bySlug();
      this.cuts();
      this.highlighted();
      this.layer?.setStyle((f) => this.style(f as Boundary));
    });

    inject(DestroyRef).onDestroy(() => this.map?.remove());
  }

  private style(feature: Boundary): L.PathOptions {
    const slug = feature.properties.slug;
    const lit = this.highlighted() === slug;
    return {
      fillColor: fillFor(this.bySlug().get(slug), this.cuts()),
      fillOpacity: 0.88,
      color: lit ? '#10242B' : '#FFFFFF',
      weight: lit ? 2.5 : 0.8,
    };
  }

  private wire(feature: Boundary, layer: L.Path): void {
    const slug = feature.properties.slug;
    const enter = () => {
      this.highlighted.set(slug);
      layer.bringToFront();
      this.hovered.emit(slug);
    };
    const leave = () => {
      if (this.highlighted() === slug) {
        this.highlighted.set(null);
        this.hovered.emit(null);
      }
    };
    layer.on({ mouseover: enter, mouseout: leave, click: enter });
    // Keyboard: each neighborhood is a focusable shape named for screen readers.
    layer.on('add', () => {
      const element = layer.getElement();
      if (element) {
        element.setAttribute('tabindex', '0');
        element.setAttribute('role', 'img');
        element.setAttribute('aria-label', feature.properties.NAME);
        element.addEventListener('focus', enter);
        element.addEventListener('blur', leave);
      }
    });
  }
}
