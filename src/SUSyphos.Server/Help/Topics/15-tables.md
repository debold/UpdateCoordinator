# Working with lists {#top}

All lists of the dashboard (servers, updates, ring members, approvals, agents, audit log and others) work the same way: search, group, sort, filter and page through them. The current view is part of the address, so a filtered list can be bookmarked or sent to a colleague.

## Search, grouping and filter chips {#tools}

The bar above a list:

- **Search**: type part of a name, domain, KB number or title (each list says in the field what it searches) and press Enter or **Apply**. The search ignores upper and lower case.
- **Group by**: where offered, puts the rows into groups with a heading row per value and the number of rows in it, for example "Group by operating system". "No grouping" shows a plain list. Choose a value and press **Apply**.
- **Apply**: runs search and grouping. Filters and sorting stay as they are.
- **Filter chips**: each active column filter appears as a chip, for example "Status: Failed". The **×** on a chip removes that one value.

## Sorting {#sort}

Click a column title to sort by it; click again to reverse the order. A small arrow marks the column and direction. Columns without a link cannot be sorted.

## Column filters {#filter}

The funnel icon next to a column title opens the filter of that column. It lists every value in the column with the number of rows that have it, so you see at once how many servers run which operating system or how many updates are in each classification.

![Filter drop-down of the "Status" column in the server list, with search field, values and counts](help-images/tables-filter.png)

- Tick one or more values and press **Apply**. Rows with any of the ticked values remain (several values of one column are combined with "or"; filters of different columns with "and").
- **(none)** stands for rows without a value, for example servers in no group.
- With more than six values a search field appears at the top; it narrows the list of values while you type (needs the dashboard script).
- **Clear** removes the filter of this column.
- A funnel that is filled in marks a column with an active filter.

Tiles and links elsewhere in the dashboard open lists with filters already set; the chips show which ones.

## Selecting rows {#select}

Lists with actions for several rows at once (servers, updates, approvals) have a checkbox in front of each row. **Select** the rows, then use the button above the list; it applies to the selected rows only. **Select all** in the header ticks or clears every row shown on the page (needs the dashboard script). The selection is not kept when you change page, search or filters.

## Pages {#pages}

Long lists are split into pages of 25 rows (50 in the audit log). The line below the list says which rows are shown and how many match ("26–50 of 312 servers", with "filtered from …" when a search or filter hides rows); the page numbers and the arrows move between pages. Search, filters and sorting apply to the whole list, not only to the page.

- **Rows per page**: shows 10, 25, 50 or 100 rows per page. The choice applies at once (without the dashboard script with **Show**) and is part of the address like the rest of the view.
- **Several lists on one page**, for example on the server detail: each list keeps its own search, filters, sorting, page and page size, so paging through one list does not change the others.
