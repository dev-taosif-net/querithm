// The grid's size factor (fonts, row and header heights, padding, default widths), in its own module so layout code
// can size panes without loading the grid (Glide stays out of the startup bundle).
export const GRID_SCALE = 0.93;
/** Row and header heights the grid draws with, in CSS px. */
export const GRID_ROW_PX = Math.round(24 * GRID_SCALE);
export const GRID_HEADER_PX = Math.round(28 * GRID_SCALE);
