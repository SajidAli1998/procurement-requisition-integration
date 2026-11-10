"use client";

import { Fragment, useState } from "react";
import {
  flexRender,
  getCoreRowModel,
  getExpandedRowModel,
  getFilteredRowModel,
  getPaginationRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type ColumnFiltersState,
  type ExpandedState,
  type FilterFn,
  type PaginationState,
  type Row,
  type SortingState,
  type Table as TanTable,
  type VisibilityState,
} from "@tanstack/react-table";
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Columns3, Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect } from "@/components/ui/select";
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { asText, cn } from "@/lib/utils";

declare module "@tanstack/react-table" {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  interface ColumnMeta<TData, TValue> {
    label?: string;
    filter?: "text" | "select";
    options?: string[];
    className?: string;
  }
}

export function textFilter<T>(...[row, columnId, value]: Parameters<FilterFn<T>>): boolean {
  if (value === undefined || value === null || value === "") return true;
  const cell = asText(row.getValue(columnId)).toLowerCase();
  return cell.includes(String(value).toLowerCase());
}

export function exactFilter<T>(...[row, columnId, value]: Parameters<FilterFn<T>>): boolean {
  if (value === undefined || value === null || value === "") return true;
  if (value === "(empty)") return asText(row.getValue(columnId)) === "";
  return asText(row.getValue(columnId)) === String(value);
}

const globalText: FilterFn<unknown> = (row, columnId, value) => {
  const q = String(value ?? "").trim().toLowerCase();
  if (!q) return true;
  return asText(row.getValue(columnId)).toLowerCase().includes(q);
};

interface DataTableProps<T> {
  data: T[];
  columns: ColumnDef<T, unknown>[];
  getRowId?: (row: T, index: number) => string;
  initialSorting?: SortingState;
  initialVisibility?: VisibilityState;
  pageSizes?: number[];
  initialPageSize?: number;
  renderExpanded?: (row: Row<T>) => React.ReactNode;
  onRowClick?: (row: T) => void;
  rowClassName?: (row: T) => string | undefined;
  toolbar?: (table: TanTable<T>) => React.ReactNode;
  searchPlaceholder?: string;
  columnFilters?: boolean;
  testId?: string;
  emptyMessage?: string;
  globalFilter?: string;
  onGlobalFilterChange?: (v: string) => void;
  filters?: ColumnFiltersState;
  onFiltersChange?: (v: ColumnFiltersState) => void;
  pagination?: PaginationState;
  onPaginationChange?: (v: PaginationState) => void;
  caption?: string;
}

export function DataTable<T>({
  data,
  columns,
  getRowId,
  initialSorting = [],
  initialVisibility = {},
  pageSizes = [10, 25, 50, 100],
  initialPageSize = 25,
  renderExpanded,
  onRowClick,
  rowClassName,
  toolbar,
  searchPlaceholder = "Search all columns",
  columnFilters = false,
  testId,
  emptyMessage = "No rows match.",
  globalFilter: globalFilterProp,
  onGlobalFilterChange,
  filters: filtersProp,
  onFiltersChange,
  pagination: paginationProp,
  onPaginationChange,
  caption,
}: DataTableProps<T>) {
  const [sorting, setSorting] = useState<SortingState>(initialSorting);
  const [visibility, setVisibility] = useState<VisibilityState>(initialVisibility);
  const [expanded, setExpanded] = useState<ExpandedState>({});
  const [globalFilterState, setGlobalFilterState] = useState("");
  const [filtersState, setFiltersState] = useState<ColumnFiltersState>([]);
  const [paginationState, setPaginationState] = useState<PaginationState>({ pageIndex: 0, pageSize: initialPageSize });

  const globalFilter = globalFilterProp ?? globalFilterState;
  const filters = filtersProp ?? filtersState;
  const pagination = paginationProp ?? paginationState;

  const table = useReactTable<T>({
    data,
    columns,
    getRowId,
    state: { sorting, columnVisibility: visibility, expanded, globalFilter, columnFilters: filters, pagination },
    onSortingChange: setSorting,
    onColumnVisibilityChange: setVisibility,
    onExpandedChange: setExpanded,
    onGlobalFilterChange: (u) => {
      const v = typeof u === "function" ? u(globalFilter) : u;
      (onGlobalFilterChange ?? setGlobalFilterState)(String(v ?? ""));
    },
    onColumnFiltersChange: (u) => {
      const v = typeof u === "function" ? u(filters) : u;
      (onFiltersChange ?? setFiltersState)(v);
    },
    onPaginationChange: (u) => {
      const v = typeof u === "function" ? u(pagination) : u;
      (onPaginationChange ?? setPaginationState)(v);
    },
    globalFilterFn: globalText as FilterFn<T>,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    getPaginationRowModel: getPaginationRowModel(),
    getExpandedRowModel: getExpandedRowModel(),
    getRowCanExpand: () => !!renderExpanded,
    autoResetPageIndex: false,
  });

  const total = data.length;
  const filtered = table.getFilteredRowModel().rows.length;
  const { pageIndex, pageSize } = table.getState().pagination;
  const pageCount = Math.max(1, table.getPageCount());
  // Keep the page in range when filters shrink the result set.
  if (pageIndex > 0 && pageIndex >= pageCount) queueMicrotask(() => table.setPageIndex(pageCount - 1));
  const visibleColumns = table.getVisibleLeafColumns().length;

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative w-full sm:w-72">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input
            value={globalFilter}
            onChange={(e) => {
              table.setGlobalFilter(e.target.value);
              table.setPageIndex(0);
            }}
            placeholder={searchPlaceholder}
            aria-label="Search table"
            className="pl-8"
            data-testid="table-search"
          />
        </div>
        {toolbar?.(table)}
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="outline" size="sm" className="ml-auto" aria-label="Choose columns">
              <Columns3 /> Columns
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuLabel>Visible columns</DropdownMenuLabel>
            <DropdownMenuSeparator />
            {table
              .getAllLeafColumns()
              .filter((c) => c.getCanHide())
              .map((c) => (
                <DropdownMenuCheckboxItem key={c.id} checked={c.getIsVisible()} onCheckedChange={(v) => c.toggleVisibility(!!v)} onSelect={(e) => e.preventDefault()}>
                  {c.columnDef.meta?.label ?? c.id}
                </DropdownMenuCheckboxItem>
              ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>

      <div className="overflow-x-auto rounded-md border">
        <table className="w-full text-sm" data-testid={testId ? `${testId}-table` : undefined}>
          {caption ? <caption className="sr-only">{caption}</caption> : null}
          <thead className="bg-muted/40">
            {table.getHeaderGroups().map((hg) => (
              <tr key={hg.id} className="border-b">
                {hg.headers.map((h) => {
                  const sorted = h.column.getIsSorted();
                  const canSort = h.column.getCanSort();
                  return (
                    <th
                      key={h.id}
                      scope="col"
                      aria-sort={sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : undefined}
                      className={cn("h-9 px-2 text-left align-middle text-xs font-semibold text-muted-foreground whitespace-nowrap", h.column.columnDef.meta?.className)}
                    >
                      {h.isPlaceholder ? null : canSort ? (
                        <button
                          type="button"
                          className="inline-flex items-center gap-1 rounded hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                          onClick={h.column.getToggleSortingHandler()}
                        >
                          {flexRender(h.column.columnDef.header, h.getContext())}
                          {sorted === "asc" ? <ArrowUp className="size-3" /> : sorted === "desc" ? <ArrowDown className="size-3" /> : <ArrowUpDown className="size-3 opacity-40" />}
                        </button>
                      ) : (
                        flexRender(h.column.columnDef.header, h.getContext())
                      )}
                    </th>
                  );
                })}
              </tr>
            ))}
            {columnFilters ? (
              <tr className="border-b">
                {table.getVisibleLeafColumns().map((c) => {
                  const meta = c.columnDef.meta;
                  const label = meta?.label ?? c.id;
                  return (
                    <th key={c.id} className="px-1 py-1 align-top font-normal">
                      {meta?.filter === "select" ? (
                        <NativeSelect
                          aria-label={`Filter ${label}`}
                          value={String(c.getFilterValue() ?? "")}
                          onChange={(e) => {
                            c.setFilterValue(e.target.value || undefined);
                            table.setPageIndex(0);
                          }}
                          className="min-w-24 [&_select]:h-7 [&_select]:text-xs"
                        >
                          <option value="">All</option>
                          {(meta.options ?? []).map((o) => (
                            <option key={o} value={o}>
                              {o}
                            </option>
                          ))}
                        </NativeSelect>
                      ) : meta?.filter === "text" ? (
                        <Input
                          aria-label={`Filter ${label}`}
                          value={String(c.getFilterValue() ?? "")}
                          onChange={(e) => {
                            c.setFilterValue(e.target.value || undefined);
                            table.setPageIndex(0);
                          }}
                          placeholder="Filter"
                          className="h-7 min-w-20 text-xs"
                        />
                      ) : null}
                    </th>
                  );
                })}
              </tr>
            ) : null}
          </thead>
          <tbody>
            {table.getRowModel().rows.length === 0 ? (
              <tr>
                <td colSpan={visibleColumns} className="p-6 text-center text-sm text-muted-foreground" data-testid="table-empty">
                  {emptyMessage}
                </td>
              </tr>
            ) : (
              table.getRowModel().rows.map((row) => (
                <Fragment key={row.id}>
                  <tr
                    data-row-id={row.id}
                    className={cn("border-b transition-colors hover:bg-muted/40", onRowClick && "cursor-pointer", rowClassName?.(row.original))}
                    onClick={(e) => {
                      if (!onRowClick) return;
                      const target = e.target as HTMLElement;
                      if (target.closest("button, a, input, select, label, [role=checkbox]")) return;
                      onRowClick(row.original);
                    }}
                  >
                    {row.getVisibleCells().map((cell) => (
                      <td key={cell.id} className={cn("px-2 py-1.5 align-middle", cell.column.columnDef.meta?.className)}>
                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                      </td>
                    ))}
                  </tr>
                  {row.getIsExpanded() && renderExpanded ? (
                    <tr className="border-b bg-muted/20">
                      <td colSpan={visibleColumns} className="p-3">
                        {renderExpanded(row)}
                      </td>
                    </tr>
                  ) : null}
                </Fragment>
              ))
            )}
          </tbody>
        </table>
      </div>

      <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        <span data-testid="table-count" aria-live="polite">
          {filtered === total ? `${total} rows` : `${filtered} of ${total} rows`}
        </span>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <label className="flex items-center gap-1">
            Rows per page
            <NativeSelect
              aria-label="Rows per page"
              value={String(pageSize)}
              onChange={(e) => table.setPagination({ pageIndex: 0, pageSize: Number(e.target.value) })}
              className="[&_select]:h-7 [&_select]:text-xs"
            >
              {pageSizes.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </NativeSelect>
          </label>
          <span data-testid="table-page">
            Page {Math.min(pageIndex + 1, pageCount)} of {pageCount}
          </span>
          <Button variant="outline" size="icon" className="size-7" onClick={() => table.setPageIndex(0)} disabled={!table.getCanPreviousPage()} aria-label="First page">
            <ChevronsLeft />
          </Button>
          <Button variant="outline" size="icon" className="size-7" onClick={() => table.previousPage()} disabled={!table.getCanPreviousPage()} aria-label="Previous page">
            <ChevronLeft />
          </Button>
          <Button variant="outline" size="icon" className="size-7" onClick={() => table.nextPage()} disabled={!table.getCanNextPage()} aria-label="Next page">
            <ChevronRight />
          </Button>
          <Button variant="outline" size="icon" className="size-7" onClick={() => table.setPageIndex(pageCount - 1)} disabled={!table.getCanNextPage()} aria-label="Last page">
            <ChevronsRight />
          </Button>
        </div>
      </div>
    </div>
  );
}
