# ErpTool — scope and boundaries

ErpTool lets an AI agent run a company's ERP (AI-ERP, a WebVella fork) through a
headless HTTP API. The agent talks to the ERP with plain actions: create a customer,
place an order, deliver goods, issue an invoice, collect a payment, read a report.

This page states clearly what ErpTool covers and what it does **not** cover, so no
one expects a feature that is not there.

## What ErpTool covers

The agent can do the day-to-day commercial cycle end to end:

- **Customers and suppliers** — create, look up, update.
- **Products and stock** — create products, check stock, transfer between
  warehouses, adjust stock, reserve and release stock.
- **Sales** — quotes, sales orders, delivery, invoices, credit notes.
- **Purchases** — purchase requests, purchase orders, goods receipt, supplier
  bills, supplier credit notes, supplier returns.
- **Money in and out** — record customer payments, pay supplier bills, collect
  across several invoices at once, handle small allowances.
- **Invoice lifecycle** — proforma, post, storno (reversal), duplicate, cancel.
- **Reports** — sales by customer, sales by product, purchases by supplier,
  receivables aging (scadenzario).
- **Composed workflows** — one request can chain several steps (for example a
  full order-to-cash: order → deliver → invoice → collect).

All money is handled as exact decimal values. Prices are converted to the
customer's currency when needed.

## Headless install and setup

The ERP installs with no interactive wizard and no human installer. The whole
setup is driven by one file, `bootstrap.json`, that lists the business entities
and the starting records a company needs.

1. An operator creates an empty PostgreSQL database and starts the ERP. This is
   plain deployment, not an agent action.
2. On startup the ERP reads `bootstrap.json` and creates the schema and the seed
   data by itself. Nothing is asked of a human.
3. The agent can then check and re-drive that setup through the tool:
   - `setup_status` — is the ERP installed? how many entities? is a setup change
     pending?
   - `reprovision` — re-apply `bootstrap.json` now, without restarting. Safe to
     call again: if nothing changed, it does nothing.

So the agent can always see whether the ERP is ready, and can push a setup change
on its own. The agent does not create the database or start the server process —
those stay with deployment.

## What ErpTool does NOT cover

These are outside the tool on purpose. Each line says why.

### Full accounting (the general ledger)
ErpTool manages commercial documents, not the accounting books. It does **not**
produce or manage:
- prima nota / libro giornale (daybook / general journal)
- IVA register and IVA liquidazione (VAT register and VAT settlement)
- LIPE, F24, CU, 770 (Italian tax filings)
- bilancio (year-end financial statements)

**Why:** there is no accounting module behind these in the ERP. They need a real
double-entry ledger, which is a separate system.

### Electronic invoicing and certified delivery
ErpTool creates invoice records, but it does **not**:
- generate the e-invoice XML file (SdI / SDI format)
- send through PEC (certified email)
- produce or email a PDF of a document

**Why:** these are document-output and transmission features, not data actions.
They belong to a printing/e-invoicing service, not the agent's data API.

### User interface and devices
ErpTool is a **headless API**. It has no screen. It does **not** handle:
- login screens, dashboards, or any UI
- tablet or point-of-sale layouts
- barcode-scanner input

**Why:** the agent uses the API directly. A human-facing UI is a different
product.

### Production and MRP
ErpTool does **not** run:
- manufacturing / production orders
- MRP (material requirements planning)

**Why:** the ERP instance in scope is a trading/distribution setup, not a
manufacturing one.

### Infrastructure and administration
ErpTool does **not** manage:
- user accounts, roles, and permissions
- access logs and audit administration
- database backup and restore
- provisioning the database or starting the server process (plain deployment)

**Why:** these are IT/infrastructure tasks, not business actions an agent takes
on company data. The application-level setup (schema and seed) is covered above
under *Headless install and setup*.

## Summary

ErpTool covers the commercial order-to-cash and purchase-to-pay cycle, the core
reports around it, and the headless install/setup of the application (schema and
seed). It does not cover the accounting ledger, e-invoice transmission, any user
interface, manufacturing, or IT administration.
