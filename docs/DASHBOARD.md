# DASHBOARD.md

Candidate dashboards for the ClickHouse retail warehouse, with ready-to-run aggregation queries.

## Conventions used in every query
- Replace `{company_id}` with the tenant id (from the `X-Company-Id` header). **Always** filter by it — every table is multi-tenant.
- Replace `{from}` / `{to}` with the date window, e.g. `'2026-01-01'` / `'2026-07-05'`. Most queries default to the last 90 days via `today() - 90`.
- Date columns are `DateTime64(3)` — wrap with `toDate(...)` when grouping by day.
- Money: use the **accounting-currency** columns (`*_acy` / `*_ac`) for cross-currency comparability; the `*_lcy`/`*_lc` columns are local currency.
- Amounts/quantities in `item_association`, `*_price_elasticity`, `price_pack_architecture` are stored as `String` → wrap with `toFloat64OrNull(...)`.
- These are read-only analytical queries; every result set is a small aggregation suitable for a card, chart, or short table.

---

## 1. Sales Performance Overview
Source: `sales_line` (line-level revenue), `sales_header_details` (order/basket & payment).

**1a. Daily sales trend (revenue, units, baskets)**
```sql
SELECT toDate(date)                                            AS day,
       sum(net_amount_acy)                                     AS revenue,
       sum(quantity)                                           AS units,
       uniqExact((store_code, pos_number, transaction_no, receipt_no)) AS baskets,
       round(sum(net_amount_acy) / nullIf(uniqExact((store_code, pos_number, transaction_no, receipt_no)), 0), 2) AS avg_basket_value
FROM sales_line
WHERE company_id = '{company_id}'
  AND toDate(date) >= today() - 90
GROUP BY day
ORDER BY day;
```

**1b. Revenue by store (top 20)**
```sql
SELECT s.store                        AS store,
       sum(l.net_amount_acy)          AS revenue,
       sum(l.quantity)                AS units,
       sum(l.total_discount_acy)      AS discount
FROM sales_line AS l
LEFT JOIN store AS s
       ON s.company_id = l.company_id AND s.code = l.store_code
WHERE l.company_id = '{company_id}'
  AND toDate(l.date) >= today() - 90
GROUP BY store
ORDER BY revenue DESC
LIMIT 20;
```

**1c. Revenue by product category**
```sql
SELECT coalesce(ph.category, i.category_code, 'Unknown') AS category,
       sum(l.net_amount_acy)                             AS revenue,
       sum(l.quantity)                                   AS units
FROM sales_line AS l
LEFT JOIN item_details AS i
       ON i.company_id = l.company_id AND i.item_no = l.item_no AND i.variant_code = l.variant_code
LEFT JOIN product_hierarchy AS ph
       ON ph.company_id = i.company_id AND ph.category_code = i.category_code
WHERE l.company_id = '{company_id}'
  AND toDate(l.date) >= today() - 90
GROUP BY category
ORDER BY revenue DESC;
```

**1d. Sales by hour of day (staffing / traffic)**
```sql
SELECT toHour(time)          AS hour_of_day,
       sum(net_amount_acy)   AS revenue,
       sum(quantity)         AS units
FROM sales_line
WHERE company_id = '{company_id}'
  AND toDate(date) >= today() - 30
GROUP BY hour_of_day
ORDER BY hour_of_day;
```

**1e. Payment method mix**
```sql
SELECT coalesce(payment_method, 'Unknown') AS payment_method,
       count()                             AS orders,
       sum(delivery_charge)                AS delivery_charges
FROM sales_header_details
WHERE company_id = '{company_id}'
  AND toDate(date) >= today() - 30
GROUP BY payment_method
ORDER BY orders DESC;
```

**1f. Top 20 selling items**
```sql
SELECT l.item_no,
       any(i.description)      AS description,
       sum(l.quantity)         AS units,
       sum(l.net_amount_acy)   AS revenue
FROM sales_line AS l
LEFT JOIN item_details AS i
       ON i.company_id = l.company_id AND i.item_no = l.item_no AND i.variant_code = l.variant_code
WHERE l.company_id = '{company_id}'
  AND toDate(l.date) >= today() - 90
GROUP BY l.item_no
ORDER BY revenue DESC
LIMIT 20;
```

---

## 2. Profitability
Source: `item_profitability` (daily front/back/gross/commercial/net profit per item/location).

**2a. Profit trend (all profit layers)**
```sql
SELECT toDate(posting_date)   AS day,
       sum(amount_ac)         AS sales,
       sum(front_profit)      AS front_profit,
       sum(back_profit)       AS back_profit,
       sum(gross_profit)      AS gross_profit,
       sum(net_profit)        AS net_profit,
       round(100 * sum(net_profit) / nullIf(sum(amount_ac), 0), 2) AS net_margin_pct
FROM item_profitability
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 90
GROUP BY day
ORDER BY day;
```

**2b. Margin % by category**
```sql
SELECT coalesce(ph.category, i.category_code, 'Unknown') AS category,
       sum(p.amount_ac)                                  AS sales,
       sum(p.gross_profit)                               AS gross_profit,
       round(100 * sum(p.gross_profit) / nullIf(sum(p.amount_ac), 0), 2) AS gross_margin_pct
FROM item_profitability AS p
LEFT JOIN item_details AS i
       ON i.company_id = p.company_id AND i.item_no = p.item_no AND i.variant_code = p.variant_code
LEFT JOIN product_hierarchy AS ph
       ON ph.company_id = i.company_id AND ph.category_code = i.category_code
WHERE p.company_id = '{company_id}'
  AND toDate(p.posting_date) >= today() - 90
GROUP BY category
ORDER BY gross_profit DESC;
```

**2c. Profit by store/location**
```sql
SELECT location_code,
       sum(amount_ac)     AS sales,
       sum(net_profit)    AS net_profit,
       round(100 * sum(net_profit) / nullIf(sum(amount_ac), 0), 2) AS net_margin_pct
FROM item_profitability
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 90
GROUP BY location_code
ORDER BY net_profit DESC
LIMIT 20;
```

**2d. Least profitable items (margin leak)**
```sql
SELECT item_no,
       variant_code,
       sum(amount_ac)   AS sales,
       sum(net_profit)  AS net_profit
FROM item_profitability
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 90
GROUP BY item_no, variant_code
HAVING sales > 0
ORDER BY net_profit ASC
LIMIT 20;
```

---

## 3. Out-of-Stock & Availability
Source: `oos` (daily snapshot with `on_hand`, `velocity`, `lost`, `fill_rate`, `days_out`, `as_of_date`).

**3a. Fill rate & lost sales trend**
```sql
SELECT as_of_date,
       round(avg(fill_rate), 3)          AS avg_fill_rate,
       sum(lost)                          AS lost_sales,
       countIf(state = 'OOS')             AS oos_lines
FROM oos
WHERE company_id = '{company_id}'
  AND as_of_date >= today() - 60
GROUP BY as_of_date
ORDER BY as_of_date;
```

**3b. OOS by region / store (latest snapshot)**
```sql
SELECT region,
       store,
       round(avg(fill_rate), 3)  AS avg_fill_rate,
       countIf(state = 'OOS')    AS oos_items,
       sum(lost)                 AS lost_sales
FROM oos
WHERE company_id = '{company_id}'
  AND as_of_date = (SELECT max(as_of_date) FROM oos WHERE company_id = '{company_id}')
GROUP BY region, store
ORDER BY lost_sales DESC
LIMIT 30;
```

**3c. Worst OOS items by lost value (latest snapshot)**
```sql
SELECT item_no, product, category, supplier,
       sum(lost)      AS lost_value,
       max(days_out)  AS days_out
FROM oos
WHERE company_id = '{company_id}'
  AND as_of_date = (SELECT max(as_of_date) FROM oos WHERE company_id = '{company_id}')
  AND state = 'OOS'
GROUP BY item_no, product, category, supplier
ORDER BY lost_value DESC
LIMIT 25;
```

**3d. Days-out distribution (histogram)**
```sql
SELECT multiIf(days_out = 0, '0',
               days_out <= 2, '1-2',
               days_out <= 7, '3-7',
               days_out <= 14, '8-14', '15+') AS days_out_bucket,
       count() AS items
FROM oos
WHERE company_id = '{company_id}'
  AND as_of_date = (SELECT max(as_of_date) FROM oos WHERE company_id = '{company_id}')
GROUP BY days_out_bucket
ORDER BY items DESC;
```

---

## 4. Inventory Movements
Source: `transaction` (item ledger: `entry_type`, `quantity`, `amount_ac`, `cost_amount_ac`).

**4a. Movement volume by entry type**
```sql
SELECT coalesce(entry_type, 'Unknown') AS entry_type,
       count()                          AS entries,
       sum(quantity)                    AS quantity,
       sum(cost_amount_ac)              AS cost_value
FROM transaction
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 90
GROUP BY entry_type
ORDER BY entries DESC;
```

**4b. Daily cost of goods movement by category**
```sql
SELECT toDate(posting_date)             AS day,
       coalesce(item_category, 'Unknown') AS category,
       sum(cost_amount_ac)              AS cost_value
FROM transaction
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 30
GROUP BY day, category
ORDER BY day, cost_value DESC;
```

---

## 5. Purchasing & Vendor Performance
Source: `purchase_receipt_line` (ordered vs received), `vendor`.

**5a. Purchase value by vendor**
```sql
SELECT coalesce(v.name, p.vendor_no) AS vendor,
       sum(p.amount_lc)              AS purchase_value,
       sum(p.quantity_received)      AS qty_received
FROM purchase_receipt_line AS p
LEFT JOIN vendor AS v
       ON v.company_id = p.company_id AND v.vendor_no = p.vendor_no
WHERE p.company_id = '{company_id}'
  AND toDate(p.posting_date) >= today() - 90
GROUP BY vendor
ORDER BY purchase_value DESC
LIMIT 25;
```

**5b. PO fulfillment rate (received vs ordered) by vendor**
```sql
SELECT coalesce(v.name, p.vendor_no) AS vendor,
       sum(p.quantity_ordered)       AS ordered,
       sum(p.quantity_received)      AS received,
       round(100 * sum(p.quantity_received) / nullIf(sum(p.quantity_ordered), 0), 1) AS fulfillment_pct
FROM purchase_receipt_line AS p
LEFT JOIN vendor AS v
       ON v.company_id = p.company_id AND v.vendor_no = p.vendor_no
WHERE p.company_id = '{company_id}'
  AND toDate(p.posting_date) >= today() - 90
GROUP BY vendor
HAVING ordered > 0
ORDER BY fulfillment_pct ASC
LIMIT 25;
```

**5c. Daily receiving volume**
```sql
SELECT toDate(posting_date)      AS day,
       sum(amount_lc)            AS received_value,
       countDistinct(document_no) AS receipts
FROM purchase_receipt_line
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 60
GROUP BY day
ORDER BY day;
```

---

## 6. Transfers (inter-location logistics)
Source: `transfer_details`.

**6a. Transfer volume by route**
```sql
SELECT transfer_from_location_code AS from_loc,
       transfer_to_location_code   AS to_loc,
       sum(quantity_shipped)       AS shipped,
       sum(quantity_received)      AS received
FROM transfer_details
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 90
GROUP BY from_loc, to_loc
ORDER BY shipped DESC
LIMIT 30;
```

**6b. In-transit gap (shipped not yet received)**
```sql
SELECT transfer_to_location_code AS to_loc,
       sum(quantity_shipped - quantity_received) AS in_transit_units
FROM transfer_details
WHERE company_id = '{company_id}'
  AND toDate(posting_date) >= today() - 90
GROUP BY to_loc
HAVING in_transit_units > 0
ORDER BY in_transit_units DESC;
```

---

## 7. Warehouse Operations (picking productivity)
Source: `work_details`.

**7a. Picker productivity**
```sql
SELECT user_id,
       count()                     AS lines_worked,
       sum(quantity_picked)        AS units_picked,
       round(avg(actual_time), 2)  AS avg_time_per_line,
       countIf(is_skipped = 1)     AS skipped
FROM work_details
WHERE company_id = '{company_id}'
  AND toDate(created_on) >= today() - 30
GROUP BY user_id
ORDER BY units_picked DESC
LIMIT 25;
```

**7b. Pick accuracy (picked vs allocated)**
```sql
SELECT toDate(created_on)          AS day,
       sum(quantity_allocated)     AS allocated,
       sum(quantity_picked)        AS picked,
       round(100 * sum(quantity_picked) / nullIf(sum(quantity_allocated), 0), 1) AS pick_rate_pct
FROM work_details
WHERE company_id = '{company_id}'
  AND toDate(created_on) >= today() - 30
GROUP BY day
ORDER BY day;
```

**7c. Work status breakdown**
```sql
SELECT coalesce(line_status, 'Unknown') AS line_status,
       count() AS lines
FROM work_details
WHERE company_id = '{company_id}'
  AND toDate(created_on) >= today() - 30
GROUP BY line_status
ORDER BY lines DESC;
```

---

## 8. Incidents & Service Quality
Source: `incident`.

**8a. Incidents by category & status**
```sql
SELECT coalesce(category, 'Unknown')  AS category,
       coalesce(status, 'Unknown')    AS status,
       count()                        AS incidents,
       sum(compensate_amount * coalesce(exchange_rate, 1)) AS compensation_value
FROM incident
WHERE company_id = '{company_id}'
  AND toDate(created_on) >= today() - 90
GROUP BY category, status
ORDER BY incidents DESC;
```

**8b. Compensation trend & VIP share**
```sql
SELECT toDate(created_on)                          AS day,
       count()                                     AS incidents,
       sum(compensate_amount * coalesce(exchange_rate, 1)) AS compensation_value,
       countIf(is_vip_customer = 1)                AS vip_incidents
FROM incident
WHERE company_id = '{company_id}'
  AND toDate(created_on) >= today() - 90
GROUP BY day
ORDER BY day;
```

**8c. Average resolution time (hours) by category**
```sql
SELECT coalesce(category, 'Unknown') AS category,
       count()                       AS resolved,
       round(avg(dateDiff('hour', created_on, resolved_on)), 1) AS avg_resolution_hours
FROM incident
WHERE company_id = '{company_id}'
  AND resolved_on IS NOT NULL
  AND toDate(created_on) >= today() - 90
GROUP BY category
ORDER BY avg_resolution_hours DESC;
```

---

## 9. Customer Analytics
Source: `customer`.

**9a. New customers over time**
```sql
SELECT toStartOfMonth(customer_creation_date) AS month,
       count() AS new_customers
FROM customer
WHERE company_id = '{company_id}'
  AND customer_creation_date >= today() - 365
GROUP BY month
ORDER BY month;
```

**9b. Customer base by classification / type**
```sql
SELECT coalesce(customer_classification, 'Unknown') AS classification,
       coalesce(customer_type, 'Unknown')           AS type,
       count()                                       AS customers,
       countIf(is_blocked = 1)                       AS blocked
FROM customer
WHERE company_id = '{company_id}'
GROUP BY classification, type
ORDER BY customers DESC;
```

**9c. Loyalty membership coverage**
```sql
SELECT countIf(club_code != '' AND club_code IS NOT NULL) AS members,
       countIf(club_code = '' OR club_code IS NULL)        AS non_members,
       count()                                             AS total
FROM customer
WHERE company_id = '{company_id}';
```

---

## 10. Targets vs Actual
Source: `target` (by store/category/date) + `sales_line` actuals.

**10a. Monthly target vs actual by store**
```sql
WITH actual AS (
    SELECT store_code, toStartOfMonth(date) AS month, sum(net_amount_acy) AS actual_sales
    FROM sales_line
    WHERE company_id = '{company_id}' AND toDate(date) >= today() - 180
    GROUP BY store_code, month
),
tgt AS (
    SELECT store_code, toStartOfMonth(date) AS month, sum(target) AS target
    FROM target
    WHERE company_id = '{company_id}' AND toDate(date) >= today() - 180
    GROUP BY store_code, month
)
SELECT coalesce(a.store_code, t.store_code) AS store_code,
       coalesce(a.month, t.month)           AS month,
       t.target                             AS target,
       a.actual_sales                       AS actual,
       round(100 * a.actual_sales / nullIf(t.target, 0), 1) AS achievement_pct
FROM tgt AS t
FULL JOIN actual AS a ON a.store_code = t.store_code AND a.month = t.month
ORDER BY month, store_code;
```

---

## 11. Pricing & Promotions Intelligence
Source: `own_price_elasticity`, `cross_price_elasticity`, `price_pack_architecture`, `item_association`, `promotion`.

**11a. Price elasticity class distribution**
```sql
SELECT coalesce(elasticity_class, 'Unknown') AS elasticity_class,
       count()                               AS items,
       round(avg(toFloat64OrNull(elasticity)), 3) AS avg_elasticity
FROM own_price_elasticity
WHERE company_id = '{company_id}'
GROUP BY elasticity_class
ORDER BY items DESC;
```

**11b. Price-Pack-Architecture class mix (share of revenue)**
```sql
SELECT coalesce(ppa_class, 'Unknown')            AS ppa_class,
       count()                                    AS items,
       round(sum(toFloat64OrNull(total_revenue)), 2) AS revenue
FROM price_pack_architecture
WHERE company_id = '{company_id}'
GROUP BY ppa_class
ORDER BY revenue DESC;
```

**11c. Top product associations (market basket, by lift)**
```sql
SELECT antecedents,
       consequents,
       round(toFloat64OrNull(support), 4)    AS support,
       round(toFloat64OrNull(confidence), 4) AS confidence,
       round(toFloat64OrNull(lift), 3)       AS lift
FROM item_association
WHERE company_id = '{company_id}'
  AND toFloat64OrNull(lift) > 1
ORDER BY lift DESC
LIMIT 30;
```

**11d. Cross-price relationship counts (substitutes vs complements)**
```sql
SELECT coalesce(relationship_type, 'Unknown') AS relationship_type,
       count() AS pairs
FROM cross_price_elasticity
WHERE company_id = '{company_id}'
GROUP BY relationship_type
ORDER BY pairs DESC;
```

**11e. Active promotions over time**
```sql
SELECT toDate(from_date)          AS starts,
       count()                    AS promotions,
       countDistinct(item_no)     AS items_on_promo
FROM promotion
WHERE company_id = '{company_id}'
  AND to_date >= today() - 90
GROUP BY starts
ORDER BY starts;
```

---

## 12. Currency Watch (Lebanon context)
Source: `black_market_rate`.

**12a. Black-market vs Syrafa rate trend**
```sql
SELECT toDate(date)             AS day,
       currency_code,
       avg(black_market_rate)   AS black_market_rate,
       avg(syrafa_rate)         AS syrafa_rate
FROM black_market_rate
WHERE company_id = '{company_id}'
  AND toDate(date) >= today() - 180
GROUP BY day, currency_code
ORDER BY day;
```

---

## 13. Sales Forecast vs Actual
Source: `sales_forecast_by_scheme` + `sales_line` actual (joined to `store` for `store_scheme`).

**13a. Forecast (with confidence band) vs actual by scheme**
```sql
WITH actual AS (
    SELECT s.store_scheme AS store_scheme, toDate(l.date) AS day, sum(l.net_amount_acy) AS actual_sales
    FROM sales_line AS l
    LEFT JOIN store AS s ON s.company_id = l.company_id AND s.code = l.store_code
    WHERE l.company_id = '{company_id}' AND toDate(l.date) >= today() - 60
    GROUP BY store_scheme, day
)
SELECT toDate(f.date)                            AS day,
       f.store_scheme,
       toFloat64OrNull(f.net_amount_acy)         AS forecast,
       toFloat64OrNull(f.net_amount_acy_lower)   AS forecast_lower,
       toFloat64OrNull(f.net_amount_acy_upper)   AS forecast_upper,
       a.actual_sales                            AS actual
FROM sales_forecast_by_scheme AS f
LEFT JOIN actual AS a ON a.store_scheme = f.store_scheme AND a.day = toDate(f.date)
WHERE toDate(f.date) >= today() - 60
ORDER BY day, f.store_scheme;
```

---

## 14. Daily Sales (drill-down) ✅ built — `/dashboards/daily-sales`
Source: `sales_line` joined to `store` (scheme/name) and `item_details` → `product_hierarchy` (division/category).

Interactive dashboard: KPI cards (Sales, Transactions, Avg basket) with **vs last month** and **vs last year**,
over a table that expands **store scheme → store** inline. **Clicking a scheme or store opens a modal** with that
scope's **division → category** totals (divisions expand to categories inside the modal). A **filter** box above the
table matches scheme/store names; a second filter inside the modal matches division/category names. Division/category
aggregation is scoped by **scheme** (spanning its stores, via a `store` join) or by a single **store**.

The **displayed values depend only on Scope** — Yesterday → the anchor day, MTD → month-to-date, YTD → year-to-date
— and never change when you flip the View toggle. The **comparison window has the same shape as the displayed value**
(the scope window); **View (Day / To-Date) only changes how far the anchor is shifted back** for the vs-LM / vs-LY figures:
- **Day → weekday-aligned** ("Monday vs Monday"): shift the window **4 weeks back** (LM) and **52 weeks back** (LY) —
  i.e. `anchor − 28 days` / `anchor − 364 days`, so the compared day is the same weekday.
- **To-Date → calendar-aligned** (same date): shift the window one calendar **month** back (LM) / one calendar **year**
  back (LY).

So a single-day scope (**Yesterday**) compares **date-to-date** in the To-Date view (e.g. `Jul 5 2026` vs `Jun 5 2026`
vs `Jul 5 2025`) — not month-to-date. **MTD/YTD** compare period-to-date with the whole window shifted (e.g.
`Jun 1→anchor 2026` vs `May 1→… 2026` and `Jun 1→… 2025`). Selecting a scope defaults the basis to match it
(Yesterday → Day, MTD/YTD → To-Date); the toggle can still override. Default = Yesterday.

The table keeps each metric (Sales / Transactions / Avg basket) to a single column — value with one colored % beside
it — and a **Compare vs** toggle (Last month / Last year) switches which comparison every row's deltas show (both
windows are in the payload, so it's instant, no reload). The KPI cards show both LM and LY.

Each drill level runs one aggregation shape — `sumIf` / `uniqExactIf` compute **four** windows in a single scan: the
displayed **value** window plus the **comparison** current / last-month / last-year windows, restricted to just those
date ranges. Basket (a "transaction") = distinct `(store_code, pos_number, transaction_no, receipt_no)`.

**14a. One drill level (parameterised by scope/view/date + grouping)**
```sql
-- @vf/@vt = displayed value window (scope only). @cf/@ct, @lmf/@lmt, @lyf/@lyt = comparison
-- current / last-month / last-year windows (Day or To-Date basis). All yyyy-MM-dd.
-- Grouping column varies by level: store scheme → store_code → division_code → category_code.
SELECT coalesce(nullIf(s.store_scheme, ''), 'Unknown') AS grp_key,
       any(coalesce(nullIf(s.store_scheme, ''), 'Unknown')) AS grp_label,
       sumIf(l.net_amount_acy, toDate(l.date) BETWEEN @vf AND @vt)  AS val_sales,   -- displayed value
       uniqExactIf((l.store_code, l.pos_number, l.transaction_no, l.receipt_no), toDate(l.date) BETWEEN @vf AND @vt) AS val_tx,
       sumIf(l.net_amount_acy, toDate(l.date) BETWEEN @cf AND @ct)  AS cur_sales,
       uniqExactIf((l.store_code, l.pos_number, l.transaction_no, l.receipt_no), toDate(l.date) BETWEEN @cf AND @ct) AS cur_tx,
       sumIf(l.net_amount_acy, toDate(l.date) BETWEEN @lmf AND @lmt) AS lm_sales,
       uniqExactIf((l.store_code, l.pos_number, l.transaction_no, l.receipt_no), toDate(l.date) BETWEEN @lmf AND @lmt) AS lm_tx,
       sumIf(l.net_amount_acy, toDate(l.date) BETWEEN @lyf AND @lyt) AS ly_sales,
       uniqExactIf((l.store_code, l.pos_number, l.transaction_no, l.receipt_no), toDate(l.date) BETWEEN @lyf AND @lyt) AS ly_tx
FROM sales_line AS l
LEFT JOIN store AS s ON s.company_id = l.company_id AND s.code = l.store_code
WHERE l.company_id = '{company_id}'
  AND ( (toDate(l.date) BETWEEN @vf AND @vt)
     OR (toDate(l.date) BETWEEN @cf AND @ct)
     OR (toDate(l.date) BETWEEN @lmf AND @lmt)
     OR (toDate(l.date) BETWEEN @lyf AND @lyt) )
GROUP BY grp_key ORDER BY val_sales DESC;
```
Deeper levels add the parent filter (`store_scheme = …`, `store_code = …`, `division_code = …`) and join
`item_details` (+ a `product_hierarchy` subquery de-duplicated by `division_code` / `category_code` to avoid
row fan-out) for the division/category name. KPI cards = the sum of the scheme-level rows.

## Suggested build priority
1. **Sales Performance Overview** (#1) — highest value, drives everything.
2. **Profitability** (#2) and **Out-of-Stock** (#3) — margin + lost-sales are the classic retail levers.
3. **Targets vs Actual** (#10) and **Purchasing/Vendor** (#5) — operational accountability.
4. **Pricing & Promotions Intelligence** (#11) — differentiated analytics you already compute (elasticity, PPA, associations).
5. Remaining operational dashboards (#4, #6, #7, #8, #9, #12, #13) as needed.

> Tables intentionally excluded as dashboard sources: `transaction_old` (superseded by `transaction`), `temp_item_ids_*` (scratch), `partition_key_resolver_v` / `store_details_v` (plumbing views), `bin` / `merchandising_*` / `work` master data (better as filters/dimensions than standalone dashboards).
