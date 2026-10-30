# Working with lists {#top}

All lists of the dashboard (servers, updates, ring members, approvals, agents, audit log and others) work the same way: search, choose a view, group, sort, filter and page through them. The current state is part of the address, so a filtered list can be bookmarked or sent to a colleague.

## Search and filter chips {#tools}

The bar above a list:

- **Search**: the wide field on the left. Type part of a name, domain, KB number or title (each list says in the field what it searches) and press Enter. The search ignores upper and lower case.
- **View** and **Group**: the two buttons on the right, see below.
- **Filter chips**: the line below shows every active column filter as a chip, for example "Status: Failed" or "Rollout: not Superseded, Completed, +3" (hover for every value). Filters set by a view have a dashed outline. The **×** on a chip removes the filter of that column.
- **Reset to …**: appears next to the chips when the list does not show its default view, and brings the default view back.

## Views {#views}

Lists with recurring questions offer fixed views, for example "Open updates", "Needs attention" or "Failed or with errors". A view only sets the column filters of the list, which you then see as chips and as ticks in the column filters.

- **View**: the button with the layers icon. While the default view is shown it is only an icon (its tooltip names the view); otherwise it reads "View:" with the name and the number of rows, or **Custom** in amber.
- The menu lists every view with its number of rows and a short description; the default view is marked "default", the current one ticked.
- **Custom**: you changed a filter, so the filters no longer match a view. The menu says from which view, and **Reset to …** goes back to the default view. When you set filters by hand that match a view exactly, the list shows that view's name.
- Views are the same for everyone and cannot be saved or changed. Every list opens with its default view; search, sorting, grouping and page size do not change with the view.

## Grouping {#group}

**Group**: the button with the tree icon, where a list can be grouped. It puts the rows into groups with a heading row per value and the number of rows in it, for example by operating system. While nothing is grouped the button is only an icon; grouped it reads "Group:" with the column. **No grouping** shows a plain list.

## Sorting {#sort}

Click a column title to sort by it; click again to reverse the order. A small arrow marks the column and direction. Columns without a link cannot be sorted.

## Column filters {#filter}

The funnel icon next to a column title opens the filter of that column. It lists every value in the column with the number of rows that have it, so you see at once how many servers run which operating system or how many updates are in each classification.

![Filter drop-down of the "Status" column in the server list, with search field, values and counts](help-images/tables-filter.png)

- A ticked value is shown. Without a filter every value is ticked; untick the values to hide and press **Apply**. Rows with any of the ticked values remain (several values of one column are combined with "or"; filters of different columns with "and").
- **(none)** stands for rows without a value, for example servers in no group.
- With more than six values a search field appears at the top; it narrows the list of values while you type (needs the dashboard script).
- **All** removes the filter of this column, so every value is shown again.
- A funnel that is filled in marks a column with an active filter.

Tiles and links elsewhere in the dashboard open lists with filters already set; the chips show which ones.

## Selecting rows {#select}

Lists with actions for several rows at once (servers, updates, approvals) have a checkbox in front of each row. **Select** the rows, then use the button above the list; it applies to the selected rows only. **Select all** in the header ticks or clears every row shown on the page (needs the dashboard script). The selection is not kept when you change page, search or filters.

## Pages {#pages}

Long lists are split into pages of 25 rows (50 in the audit log). The line below the list says which rows are shown and how many match ("26–50 of 312 servers", with "filtered from …" when a search or filter hides rows); the page numbers and the arrows move between pages. Search, filters and sorting apply to the whole list, not only to the page.

- **Rows per page**: shows 10, 25, 50 or 100 rows per page. The choice applies at once (without the dashboard script with **Show**) and is part of the address like the rest of the view.
- **Several lists on one page**, for example on the server detail: each list keeps its own search, filters, sorting, page and page size, so paging through one list does not change the others.
