package registry

import (
	"fmt"
	http "net/http"
	"strings"

	"demo.local/gomini/internal/text"
)

const MaxSize = 128

const (
	A int = 1
	B = 2
)

const Bad, Also = 1, 2

var Shared = "x"

var (
	Counter int
	Limit   = 10
)

var Sink interface{ Do() error }

var _ = Shared

type Base struct {
	name string
}

func (b Base) Name() string {
	return b.name
}

type Registry struct {
	items   []string
	owner   text.Text
	handler http.Handler
	ptr     *Widget
	gen     Generic[int]
	fn      func(int) error
	Base
}

type Lookuper interface {
	Lookup(key string) string
	All() []string
	Registry
}

type Alias = Registry

type Mode string

func New() *Registry {
	zero := Registry{}
	cfg := &Registry{items: []string{}}
	fmt.Println(strings.ToUpper("x"), zero, cfg)
	http.HandleFunc("/", nil)
	text.Normalize("x", 4)
	return cfg
}

func (r *Registry) Add(item string) *Registry {
	const local = 1
	var scratch int
	r.items = append(r.items, item)
	fmt.Println(scratch, local)
	return r
}

func (r Registry) Len() int {
	return len(r.items)
}

func Map[K comparable, V any](m map[K]V) int {
	return len(m)
}

func (g *Generic[T]) Get(key string) T {
	var zero T
	return zero
}

type Generic[T any] struct {
	value T
}

func anonymous() {
	handler := func(x int) int { return x * 2 }
	_ = handler
}

func _() {}

type _ int
